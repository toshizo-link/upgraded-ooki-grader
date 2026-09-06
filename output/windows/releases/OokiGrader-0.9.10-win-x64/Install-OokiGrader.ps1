[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [string] $PackageRoot,

    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$')]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $DataRoot,

    [Parameter(Mandatory)]
    [string] $HostCertificatePath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$')]
    [string] $DnsName,

    [Parameter(Mandatory)]
    [string[]] $SchoolSubnet,

    [ValidateSet('Private', 'Domain')]
    [string] $FirewallProfile = 'Private',

    [string] $InstallRoot = "$env:ProgramFiles\Ooki Grader",

    [string] $BackupRoot,

    [switch] $BackupDestinationEncryptionConfirmed,

    [ValidateRange(1, 65535)]
    [int] $HttpsPort = 443,

    [string] $ListenAddress = '0.0.0.0',

    [string] $ServiceName = 'OokiGrader.Host',

    [ValidateScript({
        [string]::IsNullOrWhiteSpace($_) -or
        $_ -match '^[A-Fa-f0-9]{40,128}$'
    })]
    [string] $ExpectedSignerThumbprint,

    [switch] $AllowChecksumVerifiedOnSitePackage,

    [switch] $AllowUnsignedDevelopmentBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'OokiGrader.Windows.psm1') -Force

Assert-OokiWindows
if (-not $WhatIfPreference) {
    Assert-OokiAdministrator
}
if ($AllowChecksumVerifiedOnSitePackage -and
    $AllowUnsignedDevelopmentBuild) {
    throw 'Choose either the physically controlled on-site package mode or the isolated development override, not both.'
}
$allowUnsignedPackage = $AllowChecksumVerifiedOnSitePackage -or
    $AllowUnsignedDevelopmentBuild
$package = Resolve-OokiExactPath -Path $PackageRoot `
    -Purpose 'Release package root' -MustExist -PathType Directory
$install = Assert-OokiInstallRoot -InstallRoot $InstallRoot
$data = Assert-OokiDataRoot -DataRoot $DataRoot
$topology = Assert-OokiDisjointPaths -Paths @{
    'Install root' = $install
    'Data root' = $data
    'Backup root' = $BackupRoot
}
Assert-OokiServiceName -ServiceName $ServiceName | Out-Null
Assert-OokiSchoolSubnet -SchoolSubnet $SchoolSubnet | Out-Null
$certificate = Resolve-OokiExactPath -Path $HostCertificatePath `
    -Purpose 'Host certificate' -MustExist -PathType File
$hostSource = Join-Path $package 'OokiGrader.Host.exe'
$toolSource = Join-Path $package 'OokiGrader.Tool.exe'
if (-not [IO.File]::Exists($toolSource)) {
    throw 'The release payload does not contain OokiGrader.Tool.exe.'
}
$hostSignature = Assert-OokiAuthenticodeSignature -FilePath $hostSource `
    -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
    -AllowUnsignedDevelopmentBuild:$allowUnsignedPackage
$toolSignature = Assert-OokiAuthenticodeSignature -FilePath $toolSource `
    -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
    -AllowUnsignedDevelopmentBuild:$allowUnsignedPackage

if (-not [string]::IsNullOrWhiteSpace($BackupRoot) -and
    -not $BackupDestinationEncryptionConfirmed) {
    Write-Warning 'The backup volume is not confirmed as encrypted. BitLocker is recommended.'
}
$backup = if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
    $null
} else {
    Resolve-OokiExactPath -Path $BackupRoot -Purpose 'Backup root'
}
$origin = if ($HttpsPort -eq 443) {
    "https://${DnsName}"
} else {
    "https://${DnsName}:${HttpsPort}"
}
$null = Set-OokiManagedHostsEntry -DnsName $DnsName `
    -IpAddress '127.0.0.1' -WhatIf -Confirm:$false
$configurationPath = Join-Path (
    Join-Path $data 'configuration') 'appsettings.Production.json'

$preflightArguments = @{
    DataRoot = $data
    PackageRoot = $package
    Version = $Version
    HttpsPort = $HttpsPort
    ServiceName = $ServiceName
    PassThru = $true
    AllowChecksumVerifiedOnSitePackage = `
        $AllowChecksumVerifiedOnSitePackage
    AllowUnsignedDevelopmentBuild = $AllowUnsignedDevelopmentBuild
    ExpectedSignerThumbprint = $ExpectedSignerThumbprint
}
if ($null -ne $backup) {
    $preflightArguments.BackupRoot = $backup
}
$preflight = & (Join-Path $PSScriptRoot `
    'Test-OokiGraderPreflight.ps1') @preflightArguments
$preflight | ConvertTo-Json -Depth 8 | Out-Host
if ($preflight.blockingFailures -ne 0) {
    throw 'Installation preflight reported a blocking failure.'
}

$existingManifest = Read-OokiInstallationManifest -DataRoot $data
if ($null -ne $existingManifest -and (
    -not ([string] $existingManifest.installRoot).Equals(
        $install,
        [StringComparison]::OrdinalIgnoreCase) -or
    -not ([string] $existingManifest.dataRoot).Equals(
        $data,
        [StringComparison]::OrdinalIgnoreCase) -or
    -not ([string] $existingManifest.serviceName).Equals(
        $ServiceName,
        [StringComparison]::Ordinal) -or
    (-not $allowUnsignedPackage -and
        -not ([string] $existingManifest.expectedSignerThumbprint).Equals(
            $ExpectedSignerThumbprint,
            [StringComparison]::OrdinalIgnoreCase)))) {
    throw 'The requested paths or service identity do not match the persistent installation manifest.'
}
if ($null -ne $existingManifest -and
    -not ([string] $existingManifest.version).Equals(
        $Version,
        [StringComparison]::Ordinal)) {
    throw 'A different Ooki Grader version is already installed. Use the guarded upgrade workflow with a fresh verified backup.'
}
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -ne $existingService -and $null -eq $existingManifest) {
    $existingExecutable = Get-OokiServiceExecutablePath `
        -ServiceName $ServiceName
    if (-not ($existingExecutable + '\').StartsWith(
        (Join-Path $install 'versions').TrimEnd('\') + '\',
        [StringComparison]::OrdinalIgnoreCase)) {
        throw 'An unmanaged service already uses the requested Ooki Grader service name.'
    }
}

$versionRoot = Join-Path (Join-Path $install 'versions') $Version
$installationCompletePath = Join-Path (
    Join-Path $data 'operations') 'installation-complete.json'
if ($PSCmdlet.ShouldProcess(
    "$install with data at $data",
    'Install Ooki Grader Windows Service')) {
    if ([IO.Directory]::Exists($versionRoot)) {
        if ($null -ne $existingService -and
            $existingService.Status -ne 'Stopped') {
            Stop-Service -Name $ServiceName -Force
            $existingService.WaitForStatus(
                [System.ServiceProcess.ServiceControllerStatus]::Stopped,
                [TimeSpan]::FromSeconds(60))
        }
        $recoveryRoot = Join-Path (Split-Path -Parent $versionRoot) (
            'previous-incomplete-' + $Version + '-' +
            [DateTimeOffset]::UtcNow.ToString('yyyyMMddHHmmss'))
        Move-Item -LiteralPath $versionRoot -Destination $recoveryRoot
        $repairKind = if ([IO.File]::Exists($installationCompletePath)) {
            'completed same-version payload'
        } else {
            'incomplete prior installation payload'
        }
        Write-Warning "Moved the $repairKind to $recoveryRoot. The service will be repaired with the verified package."
    }
    Install-OokiVersionPayload -PackageRoot $package `
        -VersionRoot $versionRoot -Confirm:$false | Out-Null
    $hostExecutable = Join-Path $versionRoot 'OokiGrader.Host.exe'
    $toolExecutable = Join-Path $versionRoot 'OokiGrader.Tool.exe'
    if (-not [IO.File]::Exists($hostExecutable) -or
        -not [IO.File]::Exists($toolExecutable)) {
        throw 'The staged version is missing a required executable.'
    }
    Assert-OokiAuthenticodeSignature -FilePath $hostExecutable `
        -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
        -AllowUnsignedDevelopmentBuild:$allowUnsignedPackage |
        Out-Null
    Assert-OokiAuthenticodeSignature -FilePath $toolExecutable `
        -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
        -AllowUnsignedDevelopmentBuild:$allowUnsignedPackage |
        Out-Null

    if ($null -ne $existingService -and
        $existingService.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $existingService.WaitForStatus(
            [System.ServiceProcess.ServiceControllerStatus]::Stopped,
            [TimeSpan]::FromSeconds(60))
    }
    Set-OokiWindowsService -ExecutablePath $hostExecutable `
        -ContentRoot $versionRoot -ConfigurationPath $configurationPath `
        -ServiceName $ServiceName `
        -Confirm:$false
    Set-OokiInstallAcl -VersionRoot $versionRoot `
        -ServiceName $ServiceName -Confirm:$false
    Set-OokiDataAcl -DataRoot $data -ServiceName $ServiceName `
        -Confirm:$false
    if ($null -ne $backup) {
        Set-OokiBackupAcl -BackupRoot $backup -ServiceName $ServiceName `
            -Confirm:$false | Out-Null
    }
    $installedCertificate = Install-OokiHostCertificate `
        -SourcePath $certificate -DataRoot $data -DnsName $DnsName `
        -ServiceName $ServiceName -Confirm:$false

    $objects = Join-Path $data 'objects'
    $settings = [ordered]@{
        AllowedHosts = $DnsName
        Data = [ordered]@{
            Root = $data
            ObjectStore = $objects
            Incoming = (Join-Path $data 'incoming')
            Reports = (Join-Path $data 'reports')
        }
        Security = [ordered]@{
            AllowedOrigin = $origin
            RequireSecureCookies = $true
        }
        Backup = [ordered]@{
            Enabled = ($null -ne $backup)
            DestinationRoot = if ($null -eq $backup) { '' } else { $backup }
            DestinationEncryptionConfirmed = [bool] (
                $BackupDestinationEncryptionConfirmed)
            IncludeManagedScans = $false
            IncludeReports = $true
            ScheduleLocalHour = 2
            ScheduleLocalMinute = 0
        }
        Kestrel = [ordered]@{
            Endpoints = [ordered]@{
                Https = [ordered]@{
                    Url = "https://${ListenAddress}:${HttpsPort}"
                    Certificate = [ordered]@{
                        Path = $installedCertificate
                        Password = ''
                    }
                }
            }
            Certificates = [ordered]@{
                Default = [ordered]@{
                    Path = $installedCertificate
                    Password = ''
                }
            }
        }
    }
    Write-OokiJsonFile -Path $configurationPath `
        -Value $settings -Confirm:$false
    Set-OokiFirewallRule -Port $HttpsPort `
        -RemoteAddress $SchoolSubnet `
        -FirewallProfile $FirewallProfile -Confirm:$false
    $hostHostsEntry = Set-OokiManagedHostsEntry -DnsName $DnsName `
        -IpAddress '127.0.0.1' -Confirm:$false

    if ((Get-Service -Name $ServiceName).Status -ne 'Running') {
        Start-Service -Name $ServiceName
    }
    Wait-OokiService -ServiceName $ServiceName -TimeoutSeconds 90
    try {
        $health = & (Join-Path $PSScriptRoot `
            'Test-OokiGraderHealth.ps1') `
            -ToolPath (Join-Path $versionRoot 'OokiGrader.Tool.exe') `
            -DatabasePath (Join-Path $data 'ooki-grader.db') `
            -DataRoot $data `
            -ContentRoot (Join-Path $data 'objects') `
            -ReadyUri ([Uri] "${origin}/health/ready") `
            -ServiceName $ServiceName `
            -AllowPhysicalReserveDegraded `
            -TimeoutSeconds 90 `
            -PassThru
    } catch {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        throw
    }
    if ($health.state -notin @(
            'healthy',
            'physical-reserve-degraded')) {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        throw 'The service failed its database, storage, TLS, or HTTPS health check. It was stopped; staged files and data were preserved for repair.'
    }

    Write-OokiInstallationManifest -DataRoot $data -Version $Version `
        -InstallRoot $install -ServiceName $ServiceName `
        -DnsName $DnsName -HttpsPort $HttpsPort `
        -CertificatePath $installedCertificate `
        -ConfigurationPath $configurationPath `
        -FirewallProfile $FirewallProfile `
        -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
        -Confirm:$false | Out-Null
    Write-OokiJsonFile -Path $installationCompletePath -Value ([ordered]@{
        schema = 'ooki-installation-complete/v1'
        product = 'Ooki Grader'
        version = $Version
        serviceName = $ServiceName
        verifiedAt = [DateTimeOffset]::UtcNow.ToString('O')
        healthState = $health.state
    }) -Confirm:$false

    [pscustomobject]@{
        state = 'installed'
        version = $Version
        serviceName = $ServiceName
        endpoint = "${origin}/"
        firewallProfile = $FirewallProfile
        hostHostsEntry = $hostHostsEntry
        dataPreserved = $true
        healthState = $health.state
        uploadsAvailable = [bool] $health.uploadsAvailable
        hostSignature = $hostSignature.ExternalGate
        toolSignature = $toolSignature.ExternalGate
        packageTrustMode = if ($AllowChecksumVerifiedOnSitePackage) {
            'physically-controlled-checksum-verified-on-site-package'
        } elseif ($AllowUnsignedDevelopmentBuild) {
            'isolated-development-override'
        } else {
            'authenticode-signed-production-package'
        }
        externalGates = @(
            $(if ($AllowChecksumVerifiedOnSitePackage) {
                'The technician must confirm physical custody of the unsigned on-site package; the complete checksum inventory was verified before installation.'
            } else {
                'The release signature must be independently verified against the controlled release channel.'
            }),
            'Install the public CA on authorized peers and validate DNS/TLS from a peer.',
            'Complete a verified backup and isolated restore drill before production use.'
        )
    } | ConvertTo-Json -Depth 6
}
