#requires -Version 7.4

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [string] $PackageRoot = $PSScriptRoot,

    [string] $DataRoot,

    [string] $BackupRoot,

    [switch] $BackupDestinationEncryptionConfirmed,

    [ValidatePattern('^[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$')]
    [string] $DnsName = 'ooki-grader.test',

    [string] $HostIpAddress,

    [string[]] $SchoolSubnet,

    [ValidateRange(1, 65535)]
    [int] $HttpsPort = 443,

    [string] $InstallRoot = "$env:ProgramFiles\Ooki Grader",

    [string] $PeerTrustOutputRoot,

    [ValidateScript({
        [string]::IsNullOrWhiteSpace($_) -or
        $_ -match '^[A-Fa-f0-9]{40,128}$'
    })]
    [string] $ExpectedSignerThumbprint,

    [switch] $AcceptChecksumVerifiedUnsignedOnSitePackage,

    [switch] $HostAddressReservationConfirmed,

    [switch] $SchoolNetworkPrivateConfirmed,

    [switch] $InstallationConfirmed,

    [switch] $NonInteractive
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'OokiGrader.Windows.psm1') -Force

function Read-OnSiteValue {
    param(
        [Parameter(Mandatory)]
        [string] $Label,

        [string] $DefaultValue
    )

    $suffix = if ([string]::IsNullOrWhiteSpace($DefaultValue)) {
        ''
    } else {
        " [$DefaultValue]"
    }
    $value = Read-Host "$Label$suffix"
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $DefaultValue
    }
    return $value.Trim()
}

function Get-DetectedPrivateIpv4 {
    $routes = @(Get-NetRoute -AddressFamily IPv4 `
        -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
        Sort-Object RouteMetric, InterfaceMetric)
    foreach ($route in $routes) {
        $candidate = Get-NetIPAddress -AddressFamily IPv4 `
            -InterfaceIndex $route.InterfaceIndex `
            -AddressState Preferred -ErrorAction SilentlyContinue |
            Where-Object {
                $_.IPAddress -match '^10\.' -or
                $_.IPAddress -match '^192\.168\.' -or
                $_.IPAddress -match '^172\.(1[6-9]|2[0-9]|3[01])\.'
            } |
            Select-Object -First 1
        if ($null -ne $candidate) {
            return $candidate
        }
    }
    return Get-NetIPAddress -AddressFamily IPv4 `
        -AddressState Preferred -ErrorAction SilentlyContinue |
        Where-Object {
            $_.IPAddress -match '^10\.' -or
            $_.IPAddress -match '^192\.168\.' -or
            $_.IPAddress -match '^172\.(1[6-9]|2[0-9]|3[01])\.'
        } |
        Select-Object -First 1
}

function ConvertTo-NetworkCidr {
    param(
        [Parameter(Mandatory)]
        [string] $Address,

        [Parameter(Mandatory)]
        [ValidateRange(8, 32)]
        [int] $PrefixLength
    )

    $parsed = [Net.IPAddress]::Parse($Address)
    $bytes = $parsed.GetAddressBytes()
    if ($bytes.Length -ne 4) {
        throw 'Automatic school subnet calculation supports IPv4 only.'
    }
    $remainingBits = $PrefixLength
    for ($index = 0; $index -lt $bytes.Length; $index++) {
        $mask = if ($remainingBits -ge 8) {
            255
        } elseif ($remainingBits -le 0) {
            0
        } else {
            (0xff -shl (8 - $remainingBits)) -band 0xff
        }
        $bytes[$index] = $bytes[$index] -band $mask
        $remainingBits -= 8
    }
    return "$([Net.IPAddress]::new($bytes))/$PrefixLength"
}

Assert-OokiWindows
Assert-OokiAdministrator
$package = Resolve-OokiExactPath -Path $PackageRoot `
    -Purpose 'Release package root' -MustExist -PathType Directory
$inventoryPath = Join-Path $package 'release-inventory.json'
if (-not [IO.File]::Exists($inventoryPath)) {
    throw 'The release package inventory is missing.'
}
try {
    $inventory = Get-Content -LiteralPath $inventoryPath -Raw |
        ConvertFrom-Json
} catch {
    throw 'The release package inventory is not valid JSON.'
}
if ($inventory.schema -ne 'ooki-release-inventory/v1' -or
    $inventory.product -ne 'Ooki Grader' -or
    $inventory.runtime -ne 'win-x64' -or
    $inventory.version -notmatch `
        '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
    throw 'The release inventory is not a supported Ooki Grader Windows package.'
}
$version = [string] $inventory.version
$isSignedPackage = [bool] $inventory.productionSigningClaimed
if ($isSignedPackage -and
    [string]::IsNullOrWhiteSpace($ExpectedSignerThumbprint)) {
    $ExpectedSignerThumbprint = [string] $inventory.signerThumbprint
}

$detectedAddress = Get-DetectedPrivateIpv4
if ($null -eq $detectedAddress -and
    [string]::IsNullOrWhiteSpace($HostIpAddress)) {
    throw 'No private IPv4 address was detected. Connect the host to the school LAN, or supply -HostIpAddress explicitly.'
}
$defaultAddress = if ($null -eq $detectedAddress) {
    $HostIpAddress
} else {
    [string] $detectedAddress.IPAddress
}
if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $defaultDataRoot = if (Test-Path 'D:\') {
        'D:\OokiGraderData'
    } else {
        'C:\OokiGraderData'
    }
    if ($NonInteractive) {
        $DataRoot = $defaultDataRoot
    } else {
        $DataRoot = Read-OnSiteValue `
            -Label 'データ保存先' -DefaultValue $defaultDataRoot
    }
}
if ([string]::IsNullOrWhiteSpace($HostIpAddress)) {
    $HostIpAddress = if ($NonInteractive) {
        $defaultAddress
    } else {
        Read-OnSiteValue -Label '固定または DHCP 予約したホスト IP' `
            -DefaultValue $defaultAddress
    }
}
$parsedHostAddress = $null
if (-not [Net.IPAddress]::TryParse(
    $HostIpAddress,
    [ref] $parsedHostAddress) -or
    $parsedHostAddress.AddressFamily -ne `
        [Net.Sockets.AddressFamily]::InterNetwork) {
    throw 'The host address must be one exact private IPv4 address without a CIDR suffix.'
}
Assert-OokiSchoolSubnet -SchoolSubnet @($HostIpAddress) | Out-Null
$boundHostAddress = Get-NetIPAddress -AddressFamily IPv4 `
    -IPAddress $HostIpAddress -AddressState Preferred `
    -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($null -eq $boundHostAddress) {
    throw 'The selected host IPv4 address is not currently active on this Windows PC.'
}
$networkProfile = Get-NetConnectionProfile `
    -InterfaceIndex $boundHostAddress.InterfaceIndex `
    -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($null -eq $networkProfile) {
    throw 'The active school LAN connection profile could not be identified.'
}
$networkProfileRequiresPrivateChange =
    $networkProfile.NetworkCategory -notin @(
        'Private',
        'DomainAuthenticated'
    )
if ($networkProfileRequiresPrivateChange -and
    -not $SchoolNetworkPrivateConfirmed -and
    -not $WhatIfPreference) {
    if ($NonInteractive) {
        throw 'The school LAN must use the Windows Private network profile. Confirm this trusted LAN and use -SchoolNetworkPrivateConfirmed to change it.'
    }
    Write-Host "Windows network profile is $($networkProfile.NetworkCategory)."
    $privateConfirmation = Read-Host `
        'この接続が信頼できる校内 LAN で、Private に変更する場合だけ PRIVATE と入力'
    if ($privateConfirmation -cne 'PRIVATE') {
        throw 'The school LAN was not confirmed as a trusted Private network.'
    }
    $SchoolNetworkPrivateConfirmed = $true
}
$defaultSubnet = ConvertTo-NetworkCidr `
    -Address ([string] $boundHostAddress.IPAddress) `
    -PrefixLength ([int] $boundHostAddress.PrefixLength)
$firewallProfile = if (
    $networkProfile.NetworkCategory -eq 'DomainAuthenticated'
) {
    'Domain'
} else {
    'Private'
}
if ($null -eq $SchoolSubnet -or $SchoolSubnet.Count -eq 0) {
    if ($NonInteractive -and [string]::IsNullOrWhiteSpace($defaultSubnet)) {
        throw 'Non-interactive installation requires -SchoolSubnet when it cannot be detected.'
    }
    $subnetText = if ($NonInteractive) {
        $defaultSubnet
    } else {
        Read-OnSiteValue -Label '接続を許可する校内 CIDR（複数はカンマ区切り）' `
            -DefaultValue $defaultSubnet
    }
    $SchoolSubnet = @($subnetText -split ',' | ForEach-Object {
        $_.Trim()
    } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}
Assert-OokiSchoolSubnet -SchoolSubnet $SchoolSubnet | Out-Null

if ([string]::IsNullOrWhiteSpace($PeerTrustOutputRoot)) {
    $publicDocuments = Join-Path $env:PUBLIC 'Documents'
    $PeerTrustOutputRoot = Join-Path $publicDocuments `
        'OokiGrader-Client-Setup-Packages'
}
$data = Assert-OokiDataRoot -DataRoot $DataRoot
$peerOutput = Resolve-OokiExactPath -Path $PeerTrustOutputRoot `
    -Purpose 'Peer trust package output root'
$install = Assert-OokiInstallRoot -InstallRoot $InstallRoot
$backup = if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
    $null
} else {
    Resolve-OokiExactPath -Path $BackupRoot -Purpose 'Backup root'
}
Assert-OokiDisjointPaths -Paths @{
    'Install root' = $install
    'Data root' = $data
    'Backup root' = $backup
    'Peer trust output root' = $peerOutput
} | Out-Null
if ($null -ne $backup -and
    -not $BackupDestinationEncryptionConfirmed -and
    -not $WhatIfPreference) {
    if ($NonInteractive) {
        throw 'A backup destination requires -BackupDestinationEncryptionConfirmed.'
    }
    $backupConfirmation = Read-Host `
        'バックアップ先が BitLocker 等で暗号化済みなら ENCRYPTED と入力'
    if ($backupConfirmation -cne 'ENCRYPTED') {
        throw 'Backup destination encryption was not confirmed.'
    }
    $BackupDestinationEncryptionConfirmed = $true
}

if (-not $isSignedPackage -and
    -not $AcceptChecksumVerifiedUnsignedOnSitePackage -and
    -not $WhatIfPreference) {
    if ($NonInteractive) {
        throw 'This release has no Authenticode publisher signature. For a physically controlled on-site package, explicitly use -AcceptChecksumVerifiedUnsignedOnSitePackage after checking its source and checksum manifest.'
    }
    Write-Host ''
    Write-Host '注意: この配布物には Authenticode 発行元署名がありません。'
    Write-Host '同梱の全ファイルは checksum manifest で検証されますが、配布元の確認は現地担当者の責任です。'
    $unsignedConfirmation = Read-Host `
        '管理下の USB 等で直接受け取った配布物である場合だけ UNSIGNED と入力'
    if ($unsignedConfirmation -cne 'UNSIGNED') {
        throw 'Unsigned on-site package acceptance was not confirmed.'
    }
    $AcceptChecksumVerifiedUnsignedOnSitePackage = $true
}

if (-not $HostAddressReservationConfirmed -and
    -not $WhatIfPreference) {
    if ($NonInteractive) {
        throw 'Use -HostAddressReservationConfirmed after reserving or statically assigning the host IP.'
    }
    $addressConfirmation = Read-Host `
        "$HostIpAddress を固定または DHCP 予約済みなら RESERVED と入力"
    if ($addressConfirmation -cne 'RESERVED') {
        throw 'A stable host address is required because classroom PCs use a managed hosts entry.'
    }
    $HostAddressReservationConfirmed = $true
}

$origin = if ($HttpsPort -eq 443) {
    "https://${DnsName}/"
} else {
    "https://${DnsName}:${HttpsPort}/"
}
$networkSummary = if ($networkProfileRequiresPrivateChange) {
    "$($networkProfile.NetworkCategory) -> Private"
} else {
    [string] $networkProfile.NetworkCategory
}
Write-Host ''
Write-Host 'Ooki Grader 現地セットアップ'
Write-Host "  Version:       $version"
Write-Host "  URL:           $origin"
Write-Host "  Host IP:       $HostIpAddress"
Write-Host "  Firewall:      $($SchoolSubnet -join ', ')"
Write-Host "  FW profile:    $firewallProfile"
Write-Host "  Network:       $networkSummary"
Write-Host "  Data:          $data"
Write-Host "  Backup:        $(if ($null -eq $backup) { 'disabled (configure later)' } else { $backup })"
Write-Host "  Client setup:  $peerOutput"
Write-Host "  TLS:           cost-free private local CA; browser warnings are not allowed"
Write-Host "  Package trust: $(if ($isSignedPackage) { 'Authenticode signed' } else { 'physically controlled, checksum-verified on-site package' })"

if (-not $InstallationConfirmed -and -not $WhatIfPreference) {
    if ($NonInteractive) {
        throw 'Non-interactive installation requires -InstallationConfirmed.'
    }
    $confirmation = Read-Host '上記の内容でインストールするには INSTALL と入力'
    if ($confirmation -cne 'INSTALL') {
        throw 'Installation was cancelled before any service or certificate change.'
    }
    $InstallationConfirmed = $true
}

if (-not $PSCmdlet.ShouldProcess(
    "$origin on $HostIpAddress",
    'Issue private TLS certificate, configure host service/firewall, export client trust package, and run health checks')) {
    [pscustomobject]@{
        state = 'would-install'
        version = $version
        endpoint = $origin
        hostIpAddress = $HostIpAddress
        schoolSubnet = $SchoolSubnet
        firewallProfile = $firewallProfile
        dataRoot = $data
        backupRoot = $backup
        peerTrustOutputRoot = $peerOutput
        tlsMode = 'private-local-ca'
        tlsBypassUsed = $false
    } | ConvertTo-Json -Depth 6
    return
}

$allowOnSiteUnsigned = -not $isSignedPackage -and
    $AcceptChecksumVerifiedUnsignedOnSitePackage
Assert-OokiReleasePackage -PackageRoot $package `
    -ExpectedVersion $version `
    -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
    -AllowUnsignedDevelopmentBuild:$allowOnSiteUnsigned | Out-Null
$preflightArguments = @{
    DataRoot = $data
    PackageRoot = $package
    Version = $version
    HttpsPort = $HttpsPort
    ServiceName = 'OokiGrader.Host'
    PassThru = $true
    ExpectedSignerThumbprint = $ExpectedSignerThumbprint
    AllowChecksumVerifiedOnSitePackage = $allowOnSiteUnsigned
}
if ($null -ne $backup) {
    $preflightArguments.BackupRoot = $backup
}
$preflight = & (Join-Path $package `
    'Test-OokiGraderPreflight.ps1') @preflightArguments
$failedRecommendations = @($preflight.checks | Where-Object {
    -not $_.blocking -and -not $_.passed
})
if ($failedRecommendations.Count -ne 0) {
    Write-Warning '推奨構成を満たしていない項目があります。インストールは続行します。'
    foreach ($recommendation in $failedRecommendations) {
        Write-Warning "$($recommendation.name): $($recommendation.detail)"
    }
}
if ($preflight.blockingFailures -ne 0) {
    $failedChecks = @($preflight.checks | Where-Object {
        $_.blocking -and -not $_.passed
    } | ForEach-Object { $_.name }) -join ', '
    throw "On-site preflight failed before any certificate or service change: $failedChecks"
}
Set-OokiManagedHostsEntry -DnsName $DnsName `
    -IpAddress '127.0.0.1' -WhatIf -Confirm:$false | Out-Null
if ($networkProfileRequiresPrivateChange) {
    Set-NetConnectionProfile -InterfaceIndex `
        $boundHostAddress.InterfaceIndex -NetworkCategory Private
}

$certificateOutput = Join-Path $data 'certificate-issuance'
$certificateArguments = @{
    PrimaryDnsName = $DnsName
    IpAddress = @($HostIpAddress)
    OutputDirectory = $certificateOutput
    CreateLocalCa = $true
    AcknowledgeLocalCaPrivateKeyRisk = $true
    ServiceName = 'OokiGrader.Host'
    Confirm = $false
}
$certificateJson = & (Join-Path $package `
    'New-OokiGraderCertificate.ps1') @certificateArguments
$certificateMetadata = ($certificateJson | Out-String) | ConvertFrom-Json
if ($certificateMetadata.hostTrustInstalled -ne $true -or
    -not [IO.File]::Exists(
        [string] $certificateMetadata.hostCertificatePath) -or
    -not [IO.File]::Exists(
        [string] $certificateMetadata.caPublicCertificatePath)) {
    throw 'Private CA or host certificate creation did not complete safely.'
}

$hostHostsEntry = Set-OokiManagedHostsEntry -DnsName $DnsName `
    -IpAddress '127.0.0.1' -Confirm:$false

$installArguments = @{
    PackageRoot = $package
    Version = $version
    DataRoot = $data
    HostCertificatePath = [string] `
        $certificateMetadata.hostCertificatePath
    DnsName = $DnsName
    SchoolSubnet = $SchoolSubnet
    FirewallProfile = $firewallProfile
    InstallRoot = $install
    HttpsPort = $HttpsPort
    ExpectedSignerThumbprint = $ExpectedSignerThumbprint
    AllowChecksumVerifiedOnSitePackage = $allowOnSiteUnsigned
    Confirm = $false
}
if ($null -ne $backup) {
    $installArguments.BackupRoot = $backup
    $installArguments.BackupDestinationEncryptionConfirmed = $true
}
$installJson = & (Join-Path $package `
    'Install-OokiGrader.ps1') @installArguments
$installation = ($installJson | Out-String) | ConvertFrom-Json
if ($installation.state -ne 'installed') {
    throw 'The guarded Windows service installation did not report completion.'
}

$peerPackageArguments = @{
    CaCertificatePath = [string] `
        $certificateMetadata.caPublicCertificatePath
    ExpectedThumbprint = [string] $certificateMetadata.caThumbprint
    DnsName = $DnsName
    HostIpAddress = $HostIpAddress
    Endpoint = [Uri] $origin
    OutputRoot = $peerOutput
    Confirm = $false
}
$peerPackageJson = & (Join-Path $package `
    'New-OokiGraderPeerTrustPackage.ps1') @peerPackageArguments
$peerPackage = ($peerPackageJson | Out-String) | ConvertFrom-Json
if ($peerPackage.state -notin @('packaged', 'already-packaged') -or
    [bool] $peerPackage.containsPrivateKey) {
    throw 'The public-only classroom PC trust package was not created safely.'
}

$toolPath = Join-Path (Join-Path (
    Join-Path $install 'versions') $version) 'OokiGrader.Tool.exe'
$healthArguments = @(
    '-NoLogo',
    '-NoProfile',
    '-NonInteractive',
    '-File', (Join-Path $package 'Test-OokiGraderHealth.ps1'),
    '-ToolPath', $toolPath,
    '-DatabasePath', (Join-Path $data 'ooki-grader.db'),
    '-DataRoot', $data,
    '-ContentRoot', (Join-Path $data 'objects'),
    '-ReadyUri', ([Uri]::new([Uri] $origin, 'health/ready').AbsoluteUri)
)
$healthJson = & (Join-Path $PSHOME 'pwsh.exe') @healthArguments
if ($LASTEXITCODE -ne 0) {
    throw 'The installed service failed its final local database, storage, service, or HTTPS health check.'
}
$health = ($healthJson | Out-String) | ConvertFrom-Json
if ($health.state -ne 'healthy' -or
    [bool] $health.tlsBypassUsed) {
    throw 'The final health result was not securely healthy.'
}

$hostDesktop = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::CommonDesktopDirectory)
$hostShortcutPath = Join-Path $hostDesktop 'Ooki Grader.url'
$hostShortcut = @(
    '[InternetShortcut]',
    "URL=$origin",
    'IconIndex=0'
) -join "`r`n"
[IO.File]::WriteAllText(
    $hostShortcutPath,
    $hostShortcut + "`r`n",
    [Text.UTF8Encoding]::new($true))

[pscustomobject]@{
    state = 'installed-and-verified'
    version = $version
    endpoint = $origin
    hostIpAddress = $HostIpAddress
    hostHostsEntry = $hostHostsEntry
    schoolSubnet = $SchoolSubnet
    firewallProfile = $firewallProfile
    dataRoot = $data
    backup = if ($null -eq $backup) { 'disabled' } else { 'enabled' }
    backupRoot = $backup
    tlsMode = 'private-local-ca'
    caThumbprint = [string] $certificateMetadata.caThumbprint
    caPrivateKeyExported = $false
    peerTrustPackage = [string] $peerPackage.packagePath
    classroomEntryPoint = 'Install-On-This-PC.cmd'
    localHealth = $health.state
    hostShortcut = $hostShortcutPath
    peerHealthRequired = $true
    tlsBypassUsed = $false
    nextStep = 'Copy the peer trust package folder to each classroom PC and run Install-On-This-PC.cmd as administrator. Its HTTPS check must pass without a browser warning.'
} | ConvertTo-Json -Depth 8
