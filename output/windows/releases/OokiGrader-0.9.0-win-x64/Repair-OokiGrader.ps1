[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [string] $VersionRoot,

    [Parameter(Mandatory)]
    [string] $DataRoot,

    [Parameter(Mandatory)]
    [string] $HostCertificatePath,

    [Parameter(Mandatory)]
    [string[]] $SchoolSubnet,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$')]
    [string] $DnsName,

    [ValidateRange(1, 65535)]
    [int] $HttpsPort = 443,

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
$version = Resolve-OokiExactPath -Path $VersionRoot `
    -Purpose 'Installed version root' -MustExist -PathType Directory
$data = Assert-OokiDataRoot -DataRoot $DataRoot
Assert-OokiServiceName -ServiceName $ServiceName | Out-Null
Assert-OokiSchoolSubnet -SchoolSubnet $SchoolSubnet | Out-Null
$installation = Read-OokiInstallationManifest -DataRoot $data
if ($null -eq $installation -or
    -not ([string] $installation.serviceName).Equals(
        $ServiceName,
        [StringComparison]::Ordinal)) {
    throw 'Repair requires the persistent installation manifest and its immutable service identity.'
}
$expectedVersionRoot = Join-Path (
    Join-Path ([string] $installation.installRoot) 'versions') (
        [string] $installation.version)
if (-not $version.Equals(
    $expectedVersionRoot,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Repair must target the exact version recorded in the persistent installation manifest.'
}
$configurationPath = Resolve-OokiExactPath `
    -Path ([string] $installation.configurationPath) `
    -Purpose 'Persistent production configuration' `
    -MustExist -PathType File
$certificate = Resolve-OokiExactPath -Path $HostCertificatePath `
    -Purpose 'Host certificate' -MustExist -PathType File
$hostExecutable = Join-Path $version 'OokiGrader.Host.exe'
$toolExecutable = Join-Path $version 'OokiGrader.Tool.exe'
$operations = Join-Path $data 'operations'
$restoreMarker = Join-Path $operations 'restore.in-progress'
$migrationMarker = Join-Path $operations 'migration.in-progress'
if ([IO.File]::Exists($restoreMarker) -or
    [IO.File]::Exists($migrationMarker)) {
    throw 'A restore or migration is awaiting technician resolution. Normal repair will not start the service or remove the operation marker.'
}
$hostSignature = Assert-OokiAuthenticodeSignature -FilePath $hostExecutable `
    -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
    -AllowUnsignedDevelopmentBuild:$AllowUnsignedDevelopmentBuild
$toolSignature = Assert-OokiAuthenticodeSignature -FilePath $toolExecutable `
    -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
    -AllowUnsignedDevelopmentBuild:$AllowUnsignedDevelopmentBuild
$origin = if ($HttpsPort -eq 443) {
    "https://${DnsName}"
} else {
    "https://${DnsName}:${HttpsPort}"
}

if ($PSCmdlet.ShouldProcess(
    $ServiceName,
    'Repair exact service, ACL, certificate, and firewall configuration')) {
    $service = Get-Service -Name $ServiceName -ErrorAction Stop
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus(
            [System.ServiceProcess.ServiceControllerStatus]::Stopped,
            [TimeSpan]::FromSeconds(60))
    }
    Set-OokiWindowsService -ExecutablePath $hostExecutable `
        -ContentRoot $version -ConfigurationPath $configurationPath `
        -ServiceName $ServiceName -Confirm:$false
    Set-OokiInstallAcl -VersionRoot $version `
        -ServiceName $ServiceName -Confirm:$false
    Set-OokiDataAcl -DataRoot $data -ServiceName $ServiceName `
        -Confirm:$false
    $installedCertificate = Install-OokiHostCertificate `
        -SourcePath $certificate -DataRoot $data -DnsName $DnsName `
        -ServiceName $ServiceName -Confirm:$false
    try {
        $settings = Get-Content -LiteralPath $configurationPath -Raw |
            ConvertFrom-Json
        $settings.AllowedHosts = $DnsName
        $settings.Data.Root = $data
        $settings.Data.ObjectStore = Join-Path $data 'objects'
        $settings.Data.Incoming = Join-Path $data 'incoming'
        $settings.Data.Reports = Join-Path $data 'reports'
        $settings.Security.AllowedOrigin = $origin
        $settings.Security.RequireSecureCookies = $true
        $settings.Kestrel.Endpoints.Https.Url =
            "https://0.0.0.0:${HttpsPort}"
        $settings.Kestrel.Endpoints.Https.Certificate.Path =
            $installedCertificate
        $settings.Kestrel.Endpoints.Https.Certificate.Password = ''
        $settings.Kestrel.Certificates.Default.Path =
            $installedCertificate
        $settings.Kestrel.Certificates.Default.Password = ''
    } catch {
        throw 'The production service configuration is invalid and cannot be repaired safely.'
    }
    Write-OokiJsonFile -Path $configurationPath -Value $settings `
        -Confirm:$false
    Write-OokiInstallationManifest -DataRoot $data `
        -Version ([string] $installation.version) `
        -InstallRoot ([string] $installation.installRoot) `
        -ServiceName $ServiceName -DnsName $DnsName `
        -HttpsPort $HttpsPort -CertificatePath $installedCertificate `
        -ConfigurationPath $configurationPath `
        -Confirm:$false | Out-Null
    Set-OokiFirewallRule -Port $HttpsPort `
        -RemoteAddress $SchoolSubnet -Confirm:$false

    Start-Service -Name $ServiceName
    Wait-OokiService -ServiceName $ServiceName -TimeoutSeconds 90
    $health = Invoke-OokiToolJson -ToolPath $toolExecutable -Arguments @(
        'health',
        '--database',
        (Join-Path $data 'ooki-grader.db'),
        '--data-root',
        $data,
        '--content-root',
        (Join-Path $data 'objects')
    ) -AllowCheckFailure
    $ready = Test-OokiReadyEndpoint `
        -Uri ([Uri] "${origin}/health/ready") `
        -TimeoutSeconds 60
    [pscustomobject]@{
        state = if (
            $health.state -eq 'healthy' -and $ready
        ) { 'repaired' } else { 'attention-required' }
        serviceName = $ServiceName
        hostSignature = $hostSignature.ExternalGate
        toolSignature = $toolSignature.ExternalGate
        health = $health
        httpsReady = $ready
        dataPreserved = $true
    } | ConvertTo-Json -Depth 10
}
