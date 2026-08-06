[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [string] $VersionRoot,

    [Parameter(Mandatory)]
    [string] $DataRoot,

    [Parameter(Mandatory)]
    [string] $BackupDestination,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-HJKMNP-TV-Z]{26}$')]
    [string] $BackupId,

    [Parameter(Mandatory)]
    [ValidatePattern('^sets/[0-9]{4}/(?:0[1-9]|1[0-2])/[0-9A-HJKMNP-TV-Z]{26}$')]
    [string] $BackupRelativePath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Fa-f0-9]{64}$')]
    [string] $BackupManifestSha256,

    [Parameter(Mandatory)]
    [switch] $MaintenanceConfirmed,

    [Parameter(Mandatory)]
    [switch] $OfflineConfirmed,

    [Parameter(Mandatory)]
    [string] $ConfirmRestore,

    [string] $ServiceName = 'OokiGrader.Host',

    [switch] $AllowUnsignedDevelopmentBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'OokiGrader.Windows.psm1') -Force

Assert-OokiWindows
Assert-OokiAdministrator
if (-not $MaintenanceConfirmed -or -not $OfflineConfirmed) {
    throw 'Restore requires explicit maintenance and offline confirmations.'
}
if (-not $ConfirmRestore.Equals(
    $BackupId,
    [StringComparison]::Ordinal)) {
    throw 'The typed restore confirmation must exactly match the backup identifier.'
}
if (-not $BackupRelativePath.EndsWith(
    "/$BackupId",
    [StringComparison]::Ordinal)) {
    throw 'The canonical backup path must end with the selected backup identifier.'
}

$version = Resolve-OokiExactPath -Path $VersionRoot `
    -Purpose 'Installed version root' -MustExist -PathType Directory
$data = Assert-OokiDataRoot -DataRoot $DataRoot
$backup = Resolve-OokiExactPath -Path $BackupDestination `
    -Purpose 'Encrypted backup destination' -MustExist -PathType Directory
$toolExecutable = Join-Path $version 'OokiGrader.Tool.exe'
$toolSignature = Assert-OokiAuthenticodeSignature `
    -FilePath $toolExecutable `
    -AllowUnsignedDevelopmentBuild:$AllowUnsignedDevelopmentBuild
$service = Get-Service -Name $ServiceName -ErrorAction Stop
if ($service.Status -ne 'Stopped') {
    throw 'The Ooki Grader Windows Service must already be stopped; this restore script will not stop a live service implicitly.'
}

$database = Join-Path $data 'ooki-grader.db'
$content = Join-Path $data 'objects'
if ($PSCmdlet.ShouldProcess(
    $ServiceName,
    "Execute verified offline restore $BackupId and preserve rollback snapshot")) {
    $restore = Invoke-OokiToolJson `
        -ToolPath $toolExecutable `
        -Arguments @(
            'restore',
            'execute',
            '--database',
            $database,
            '--data-root',
            $data,
            '--content-root',
            $content,
            '--destination',
            $backup,
            '--destination-encryption-confirmed',
            '--backup-id',
            $BackupId,
            '--relative-path',
            $BackupRelativePath,
            '--manifest-sha256',
            $BackupManifestSha256,
            '--maintenance-confirmed',
            '--offline-confirmed',
            '--confirm-restore',
            $ConfirmRestore
        )
    if ($restore.state -ne 'restored-awaiting-signoff' -or
        -not $restore.mutationPerformed -or
        -not $restore.rollbackSnapshotCreated -or
        -not $restore.restoreMarkerPresent) {
        throw 'The restore tool did not report a complete, rollback-preserving offline restore.'
    }

    Set-OokiDataAcl -DataRoot $data -ServiceName $ServiceName `
        -Confirm:$false
    $health = Invoke-OokiToolJson `
        -ToolPath $toolExecutable `
        -Arguments @(
            'health',
            '--database',
            $database,
            '--data-root',
            $data,
            '--content-root',
            $content
        ) `
        -AllowCheckFailure
    if ($health.database.state -ne 'healthy' -or
        -not $health.database.maintenanceMode -or
        -not $health.database.schemaCurrent -or
        -not $health.storage.restoreOrMigrationMarkerPresent) {
        throw 'The restored data root failed its mandatory offline post-restore checks. Keep the service stopped and retain both roots.'
    }

    Write-OokiWindowsEvent -EventId 1201 `
        -EntryType Information `
        -Message (
            "Offline restore completed for backup $BackupId. " +
            'The service remains stopped; maintenance mode, the restore marker, ' +
            'and the rollback snapshot remain in place pending administrator sign-off.'
        ) -Confirm:$false
    [pscustomobject]@{
        state = 'restored-awaiting-signoff'
        backupId = $BackupId
        rollbackSnapshotCreated = $true
        restoreMarkerPresent = $true
        maintenanceMode = $true
        serviceState = 'Stopped'
        toolSignature = $toolSignature.ExternalGate
        providerCredentialsRequireValidation = $true
        nextActions = @(
            'Independently inspect the offline health result and rollback snapshot.',
            'Validate or re-enter provider credentials for this Windows host.',
            'Run the approved read-only verification workflow.',
            'Remove the restore marker and exit maintenance only after administrator sign-off.',
            'Retain the rollback snapshot until the restore record is accepted.'
        )
        externalGates = @(
            'Authenticode trust must be validated on the production Windows host.',
            'The Windows Service must remain stopped throughout the directory switch.',
            'A technician must complete and document a real isolated restore drill.'
        )
    } | ConvertTo-Json -Depth 8
}
