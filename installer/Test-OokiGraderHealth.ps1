[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string] $ToolPath,

    [Parameter(Mandatory)]
    [string] $DatabasePath,

    [Parameter(Mandatory)]
    [string] $DataRoot,

    [string] $ContentRoot,

    [Uri] $ReadyUri,

    [string] $ServiceName = 'OokiGrader.Host',

    [switch] $AllowPhysicalReserveDegraded,

    [ValidateRange(5, 300)]
    [int] $TimeoutSeconds = 15,

    [switch] $PassThru
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'OokiGrader.Windows.psm1') -Force

Assert-OokiWindows
$data = Assert-OokiDataRoot -DataRoot $DataRoot
$database = Resolve-OokiExactPath -Path $DatabasePath `
    -Purpose 'Database'
$objects = if ([string]::IsNullOrWhiteSpace($ContentRoot)) {
    Join-Path $data 'objects'
} else {
    Resolve-OokiExactPath -Path $ContentRoot -Purpose 'Content root'
}
$endpointHealth = $null
if ($null -ne $ReadyUri) {
    $endpointHealth = Get-OokiReadyEndpointHealth -Uri $ReadyUri `
        -TimeoutSeconds $TimeoutSeconds `
        -AllowPhysicalReserveDegraded:$AllowPhysicalReserveDegraded
}
$database = Resolve-OokiExactPath -Path $database `
    -Purpose 'Database' -MustExist -PathType File
$objects = Resolve-OokiExactPath -Path $objects `
    -Purpose 'Content root' -MustExist -PathType Directory
$toolResult = Invoke-OokiToolJson -ToolPath $ToolPath -Arguments @(
    'health',
    '--database',
    $database,
    '--data-root',
    $data,
    '--content-root',
    $objects
) -AllowCheckFailure
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$reserveOnlyDegraded = $AllowPhysicalReserveDegraded -and
    $toolResult.state -eq 'unavailable' -and
    $toolResult.database.state -eq 'healthy' -and
    [bool] $toolResult.database.schemaCurrent -and
    [bool] $toolResult.database.configuredDataRootMatches -and
    [bool] $toolResult.storage.dataRootReadable -and
    [bool] $toolResult.storage.contentRootReadable -and
    -not [bool] $toolResult.storage.restoreOrMigrationMarkerPresent -and
    -not [bool] $toolResult.storage.reserveSatisfied -and
    $toolResult.storage.errorCode -eq 'physical_reserve_not_satisfied'
$serviceRunning = $null -ne $service -and $service.Status -eq 'Running'
$healthState = if (
    $toolResult.state -eq 'healthy' -and
    $serviceRunning -and
    ($null -eq $endpointHealth -or $endpointHealth.state -eq 'healthy')
) {
    'healthy'
} elseif (
    $reserveOnlyDegraded -and
    $serviceRunning -and
    $null -ne $endpointHealth -and
    $endpointHealth.state -eq 'physical-reserve-degraded'
) {
    'physical-reserve-degraded'
} else {
    'attention-required'
}
$result = [pscustomobject]@{
    state = $healthState
    checkedAt = [DateTimeOffset]::UtcNow
    service = if ($null -eq $service) { 'missing' } else {
        [string] $service.Status
    }
    localDiagnostic = $toolResult
    httpsReady = if ($null -eq $endpointHealth) {
        $null
    } else {
        $endpointHealth.state -eq 'healthy'
    }
    httpsHealthState = if ($null -eq $endpointHealth) {
        'not-requested'
    } else {
        $endpointHealth.state
    }
    uploadsAvailable = $healthState -eq 'healthy'
    tlsBypassUsed = $false
}
if ($PassThru) {
    return $result
}
$result | ConvertTo-Json -Depth 10
if ($result.state -notin @('healthy', 'physical-reserve-degraded')) {
    exit 3
}
exit 0
