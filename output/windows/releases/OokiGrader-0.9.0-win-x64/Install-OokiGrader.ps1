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

    [switch] $AllowUnsignedDevelopmentBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'OokiGrader.Windows.psm1') -Force

Assert-OokiWindows
Assert-OokiAdministrator
$packageEvidence = Assert-OokiReleasePackage -PackageRoot $PackageRoot `
    -ExpectedVersion $Version `
    -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
    -AllowUnsignedDevelopmentBuild:$AllowUnsignedDevelopmentBuild
$package = $packageEvidence.Root
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
    -AllowUnsignedDevelopmentBuild:$AllowUnsignedDevelopmentBuild
$toolSignature = Assert-OokiAuthenticodeSignature -FilePath $toolSource `
    -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
    -AllowUnsignedDevelopmentBuild:$AllowUnsignedDevelopmentBuild

if (-not [string]::IsNullOrWhiteSpace($BackupRoot) -and
    -not $BackupDestinationEncryptionConfirmed) {
    throw 'A configured backup root requires explicit encryption confirmation.'
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
$configurationPath = Join-Path (
    Join-Path $data 'configuration') 'appsettings.Production.json'

$preflightArguments = @{
    DataRoot = $data
    PackageRoot = $package
    Version = $Version
    HttpsPort = $HttpsPort
    ServiceName = $ServiceName
    PassThru = $true
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
        [StringComparison]::Ordinal))) {
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
if ($PSCmdlet.ShouldProcess(
    "$install with data at $data",
    'Install Ooki Grader Windows Service')) {
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
        -AllowUnsignedDevelopmentBuild:$AllowUnsignedDevelopmentBuild |
        Out-Null
    Assert-OokiAuthenticodeSignature -FilePath $toolExecutable `
        -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
        -AllowUnsignedDevelopmentBuild:$AllowUnsignedDevelopmentBuild |
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
    Write-OokiInstallationManifest -DataRoot $data -Version $Version `
        -InstallRoot $install -ServiceName $ServiceName `
        -DnsName $DnsName -HttpsPort $HttpsPort `
        -CertificatePath $installedCertificate `
        -ConfigurationPath $configurationPath `
        -Confirm:$false | Out-Null
    Set-OokiFirewallRule -Port $HttpsPort `
        -RemoteAddress $SchoolSubnet -Confirm:$false

    if ((Get-Service -Name $ServiceName).Status -ne 'Running') {
        Start-Service -Name $ServiceName
    }
    Wait-OokiService -ServiceName $ServiceName -TimeoutSeconds 90
    $ready = Test-OokiReadyEndpoint `
        -Uri ([Uri] "${origin}/health/ready") `
        -TimeoutSeconds 90
    if (-not $ready) {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        throw 'The service did not pass its HTTPS readiness check. It was stopped; staged files and data were preserved for repair.'
    }

    [pscustomobject]@{
        state = 'installed'
        version = $Version
        serviceName = $ServiceName
        endpoint = "${origin}/"
        dataPreserved = $true
        hostSignature = $hostSignature.ExternalGate
        toolSignature = $toolSignature.ExternalGate
        externalGates = @(
            'The release signature must be independently verified against the controlled release channel.',
            'Install the public CA on authorized peers and validate DNS/TLS from a peer.',
            'Complete a verified backup and isolated restore drill before production use.'
        )
    } | ConvertTo-Json -Depth 6
}
