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

    [string] $ServiceName = 'OokiGrader.Host'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'OokiGrader.Windows.psm1') -Force

Assert-OokiWindows
$data = Assert-OokiDataRoot -DataRoot $DataRoot
$database = Resolve-OokiExactPath -Path $DatabasePath `
    -Purpose 'Database' -MustExist -PathType File
$objects = if ([string]::IsNullOrWhiteSpace($ContentRoot)) {
    Join-Path $data 'objects'
} else {
    Resolve-OokiExactPath -Path $ContentRoot `
        -Purpose 'Content root' -MustExist -PathType Directory
}
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
$httpsReady = $null
if ($null -ne $ReadyUri) {
    $httpsReady = Test-OokiReadyEndpoint -Uri $ReadyUri -TimeoutSeconds 15
}

$result = [pscustomobject]@{
    state = if (
        $toolResult.state -eq 'healthy' -and
        $null -ne $service -and
        $service.Status -eq 'Running' -and
        ($null -eq $httpsReady -or $httpsReady)
    ) { 'healthy' } else { 'attention-required' }
    checkedAt = [DateTimeOffset]::UtcNow
    service = if ($null -eq $service) { 'missing' } else {
        [string] $service.Status
    }
    localDiagnostic = $toolResult
    httpsReady = $httpsReady
    tlsBypassUsed = $false
}
$result | ConvertTo-Json -Depth 10
if ($result.state -ne 'healthy') {
    exit 3
}
exit 0
