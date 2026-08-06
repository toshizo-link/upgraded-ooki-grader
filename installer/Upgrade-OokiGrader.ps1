[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [string] $PackageRoot,

    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$')]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $CurrentVersionRoot,

    [Parameter(Mandatory)]
    [string] $InstallRoot,

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

    [Parameter(Mandatory)]
    [switch] $MaintenanceConfirmed,

    [Parameter(Mandatory)]
    [switch] $OfflineConfirmed,

    [Parameter(Mandatory)]
    [switch] $FreshPreUpgradeBackupConfirmed,

    [Parameter(Mandatory)]
    [Uri] $ReadyUri,

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
if (-not $MaintenanceConfirmed -or
    -not $OfflineConfirmed -or
    -not $FreshPreUpgradeBackupConfirmed) {
    throw 'Upgrade requires explicit maintenance, offline, and fresh verified backup confirmations.'
}
if ($ReadyUri.Scheme -ne 'https') {
    throw 'Upgrade readiness verification requires HTTPS.'
}

$packageEvidence = Assert-OokiReleasePackage -PackageRoot $PackageRoot `
    -ExpectedVersion $Version `
    -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
    -AllowUnsignedDevelopmentBuild:$AllowUnsignedDevelopmentBuild
$package = $packageEvidence.Root
$currentVersion = Resolve-OokiExactPath -Path $CurrentVersionRoot `
    -Purpose 'Current version root' -MustExist -PathType Directory
$install = Assert-OokiInstallRoot -InstallRoot $InstallRoot
$data = Assert-OokiDataRoot -DataRoot $DataRoot
$backup = Resolve-OokiExactPath -Path $BackupDestination `
    -Purpose 'Encrypted backup destination' -MustExist -PathType Directory
Assert-OokiDisjointPaths -Paths @{
    'Install root' = $install
    'Data root' = $data
    'Backup root' = $backup
} | Out-Null
Assert-OokiServiceName -ServiceName $ServiceName | Out-Null
$installation = Read-OokiInstallationManifest -DataRoot $data
if ($null -eq $installation -or
    -not ([string] $installation.installRoot).Equals(
        $install,
        [StringComparison]::OrdinalIgnoreCase) -or
    -not ([string] $installation.serviceName).Equals(
        $ServiceName,
        [StringComparison]::Ordinal) -or
    (-not $AllowUnsignedDevelopmentBuild -and
        -not ([string] $installation.expectedSignerThumbprint).Equals(
            $ExpectedSignerThumbprint,
            [StringComparison]::OrdinalIgnoreCase))) {
    throw 'Upgrade paths and service identity must match the persistent installation manifest.'
}
$configurationPath = Resolve-OokiExactPath `
    -Path ([string] $installation.configurationPath) `
    -Purpose 'Persistent production configuration' `
    -MustExist -PathType File
$content = Join-Path $data 'objects'
$database = Join-Path $data 'ooki-grader.db'
$currentTool = Join-Path $currentVersion 'OokiGrader.Tool.exe'
$currentHost = Join-Path $currentVersion 'OokiGrader.Host.exe'
$newHostSource = Join-Path $package 'OokiGrader.Host.exe'
$newToolSource = Join-Path $package 'OokiGrader.Tool.exe'
$newVersionRoot = Join-Path (Join-Path $install 'versions') $Version
$installedNewHost = Join-Path $newVersionRoot 'OokiGrader.Host.exe'
$installedNewTool = Join-Path $newVersionRoot 'OokiGrader.Tool.exe'
Assert-OokiAuthenticodeSignature -FilePath $newHostSource `
    -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
    -AllowUnsignedDevelopmentBuild:$AllowUnsignedDevelopmentBuild | Out-Null
Assert-OokiAuthenticodeSignature -FilePath $newToolSource `
    -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
    -AllowUnsignedDevelopmentBuild:$AllowUnsignedDevelopmentBuild | Out-Null
Assert-OokiAuthenticodeSignature -FilePath $currentHost `
    -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
    -AllowUnsignedDevelopmentBuild:$AllowUnsignedDevelopmentBuild | Out-Null
Assert-OokiAuthenticodeSignature -FilePath $currentTool `
    -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
    -AllowUnsignedDevelopmentBuild:$AllowUnsignedDevelopmentBuild | Out-Null

$configuredExecutable = Get-OokiServiceExecutablePath `
    -ServiceName $ServiceName
if ([IO.File]::Exists($installedNewHost) -and
    [IO.File]::Exists($installedNewTool) -and
    $configuredExecutable.Equals(
        $installedNewHost,
        [StringComparison]::OrdinalIgnoreCase)) {
    $existingHealth = Invoke-OokiToolJson -ToolPath $installedNewTool `
        -Arguments @(
            'health',
            '--database',
            $database,
            '--data-root',
            $data,
            '--content-root',
            $content
        ) -AllowCheckFailure
    [pscustomobject]@{
        state = if ($existingHealth.state -eq 'healthy') {
            'already-upgraded'
        } else {
            'already-upgraded-attention-required'
        }
        version = $Version
        mutationPerformed = $false
        health = $existingHealth
        previousVersionPreserved = $true
    } | ConvertTo-Json -Depth 10
    return
}
if (-not $configuredExecutable.Equals(
    $currentHost,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The configured Windows Service does not point at the declared current version.'
}

$backupVerification = Invoke-OokiToolJson -ToolPath $currentTool `
    -Arguments @(
        'backup',
        'verify',
        '--database',
        $database,
        '--content-root',
        $content,
        '--destination',
        $backup,
        '--destination-encryption-confirmed',
        '--backup-id',
        $VerifiedBackupId,
        '--relative-path',
        $VerifiedBackupRelativePath,
        '--manifest-sha256',
        $VerifiedBackupManifestSha256
    )
if (-not $backupVerification.verified) {
    throw 'The selected pre-upgrade backup did not pass verification.'
}
$beforeHealth = Invoke-OokiToolJson -ToolPath $currentTool -Arguments @(
    'health',
    '--database',
    $database,
    '--data-root',
    $data,
    '--content-root',
    $content
) -AllowCheckFailure
if ($beforeHealth.database.state -ne 'healthy' -or
    -not $beforeHealth.database.schemaCurrent -or
    -not $beforeHealth.database.maintenanceMode -or
    $beforeHealth.storage.restoreOrMigrationMarkerPresent) {
    throw 'The pre-upgrade database must be healthy, current, in maintenance mode, and free of prior operation markers.'
}
$beforeMigration = $beforeHealth.database.currentMigrationId

if ($PSCmdlet.ShouldProcess(
    "$ServiceName -> $Version",
    'Perform rollback-safe offline upgrade')) {
    Install-OokiVersionPayload -PackageRoot $package `
        -VersionRoot $newVersionRoot -Confirm:$false | Out-Null
    $newHost = Join-Path $newVersionRoot 'OokiGrader.Host.exe'
    $newTool = Join-Path $newVersionRoot 'OokiGrader.Tool.exe'
    Assert-OokiAuthenticodeSignature -FilePath $newHost `
        -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
        -AllowUnsignedDevelopmentBuild:$AllowUnsignedDevelopmentBuild |
        Out-Null
    Assert-OokiAuthenticodeSignature -FilePath $newTool `
        -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
        -AllowUnsignedDevelopmentBuild:$AllowUnsignedDevelopmentBuild |
        Out-Null
    Set-OokiInstallAcl -VersionRoot $newVersionRoot `
        -ServiceName $ServiceName -Confirm:$false
    Stop-Service -Name $ServiceName -Force
    (Get-Service -Name $ServiceName).WaitForStatus(
        [System.ServiceProcess.ServiceControllerStatus]::Stopped,
        [TimeSpan]::FromSeconds(60))
    $marker = New-OokiOperationMarker -DataRoot $data `
        -Name 'migration.in-progress' -Confirm:$false
    try {
        Set-OokiWindowsService -ExecutablePath $newHost `
            -ContentRoot $newVersionRoot `
            -ConfigurationPath $configurationPath `
            -ServiceName $ServiceName `
            -Confirm:$false
        Start-Service -Name $ServiceName
        Wait-OokiService -ServiceName $ServiceName -TimeoutSeconds 90
        if (-not (Test-OokiReadyEndpoint -Uri $ReadyUri `
            -TimeoutSeconds 90)) {
            throw 'The upgraded service did not pass readiness.'
        }
        $afterHealth = Invoke-OokiToolJson -ToolPath $newTool -Arguments @(
            'health',
            '--database',
            $database,
            '--data-root',
            $data,
            '--content-root',
            $content
        ) -AllowCheckFailure
        if ($afterHealth.database.state -ne 'healthy' -or
            -not $afterHealth.database.schemaCurrent -or
            -not $afterHealth.database.maintenanceMode -or
            -not $afterHealth.storage.restoreOrMigrationMarkerPresent) {
            throw 'The upgraded database failed migration-aware health checks while the maintenance marker was active.'
        }
        Remove-OokiOperationMarker -MarkerPath $marker -Confirm:$false
        Write-OokiInstallationManifest -DataRoot $data -Version $Version `
            -InstallRoot $install -ServiceName $ServiceName `
            -DnsName ([string] $installation.dnsName) `
            -HttpsPort ([int] $installation.httpsPort) `
            -CertificatePath ([string] $installation.certificatePath) `
            -ConfigurationPath $configurationPath `
            -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
            -Confirm:$false | Out-Null
        [pscustomobject]@{
            state = 'upgraded'
            version = $Version
            previousVersionPreserved = $true
            backupVerified = $true
            beforeMigration = $beforeMigration
            afterMigration = $afterHealth.database.currentMigrationId
            rollbackBoundary = 'An older binary is never started after an incompatible schema change.'
        } | ConvertTo-Json -Depth 8
    } catch {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        $failedHealth = Invoke-OokiToolJson -ToolPath $newTool -Arguments @(
            'health',
            '--database',
            $database,
            '--data-root',
            $data,
            '--content-root',
            $content
        ) -AllowCheckFailure
        $afterFailureMigration = if (
            $null -ne $failedHealth.PSObject.Properties['database']
        ) {
            $failedHealth.database.currentMigrationId
        } else {
            $null
        }
        if ($beforeMigration -eq $afterFailureMigration) {
            Set-OokiWindowsService -ExecutablePath $currentHost `
                -ContentRoot $currentVersion `
                -ConfigurationPath $configurationPath `
                -ServiceName $ServiceName `
                -Confirm:$false
            Start-Service -Name $ServiceName
            Wait-OokiService -ServiceName $ServiceName -TimeoutSeconds 90
            Remove-OokiOperationMarker -MarkerPath $marker -Confirm:$false
            throw 'Upgrade failed before a schema change. The prior signed binary was restored and data was preserved.'
        }

        $restorePlan = Invoke-OokiToolJson -ToolPath $newTool -Arguments @(
            'restore',
            'plan',
            '--database',
            $database,
            '--content-root',
            $content,
            '--destination',
            $backup,
            '--destination-encryption-confirmed',
            '--backup-id',
            $VerifiedBackupId,
            '--relative-path',
            $VerifiedBackupRelativePath,
            '--manifest-sha256',
            $VerifiedBackupManifestSha256
        ) -AllowCheckFailure
        $restorePlan | ConvertTo-Json -Depth 10 | Write-Warning
        throw 'Upgrade failed after the schema changed. The service remains stopped and the migration marker remains in place. Follow the validated restore plan on an isolated target; no live overwrite was attempted.'
    }
}
