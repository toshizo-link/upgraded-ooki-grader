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
    [string] $BackupDestination,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-HJKMNP-TV-Z]{26}$')]
    [string] $VerifiedBackupId,

    [Parameter(Mandatory)]
    [string] $VerifiedBackupRelativePath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Fa-f0-9]{64}$')]
    [string] $VerifiedBackupManifestSha256,

    [string] $ExpectedSignerThumbprint,

    [switch] $AllowChecksumVerifiedOnSitePackage,

    [switch] $AllowUnsignedDevelopmentBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'OokiGrader.Windows.psm1') -Force

Assert-OokiWindows
Assert-OokiAdministrator
$data = Assert-OokiDataRoot -DataRoot $DataRoot
$installation = Read-OokiInstallationManifest -DataRoot $data
if ($null -eq $installation) {
    throw 'The Ooki Grader installation manifest was not found in DataRoot.'
}

$installRoot = [string] $installation.installRoot
$currentVersion = [string] $installation.version
$currentVersionRoot = Join-Path (Join-Path $installRoot 'versions') `
    $currentVersion
$dnsName = [string] $installation.dnsName
$httpsPort = [int] $installation.httpsPort
$readyUri = [Uri] "https://${dnsName}:${httpsPort}/health/ready"
$serviceName = [string] $installation.serviceName

if ([string]::IsNullOrWhiteSpace($ExpectedSignerThumbprint)) {
    $ExpectedSignerThumbprint = [string] `
        $installation.expectedSignerThumbprint
}

$upgrade = Join-Path $PSScriptRoot 'Upgrade-OokiGrader.ps1'
$arguments = @{
    PackageRoot = $PackageRoot
    Version = $Version
    CurrentVersionRoot = $currentVersionRoot
    InstallRoot = $installRoot
    DataRoot = $data
    BackupDestination = $BackupDestination
    VerifiedBackupId = $VerifiedBackupId
    VerifiedBackupRelativePath = $VerifiedBackupRelativePath
    VerifiedBackupManifestSha256 = $VerifiedBackupManifestSha256
    MaintenanceConfirmed = $true
    OfflineConfirmed = $true
    FreshPreUpgradeBackupConfirmed = $true
    ReadyUri = $readyUri
    ServiceName = $serviceName
    ExpectedSignerThumbprint = $ExpectedSignerThumbprint
    AllowChecksumVerifiedOnSitePackage = `
        $AllowChecksumVerifiedOnSitePackage
    AllowUnsignedDevelopmentBuild = $AllowUnsignedDevelopmentBuild
    Confirm = $false
}

if ($PSCmdlet.ShouldProcess(
    "$serviceName $currentVersion -> $Version",
    'Run the verified, rollback-safe Ooki Grader host update')) {
    & $upgrade @arguments
}
