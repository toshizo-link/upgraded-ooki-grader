# Runs the real configuration script with in-process HTTP doubles. No network traffic.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$configurationScript = Join-Path $PSScriptRoot '..\..\installer\Set-OokiGraderSchoolManager.ps1'
$configurationRequests = [Collections.Generic.List[object]]::new()
function Invoke-WebRequest {
    param($Uri, $Method, $ContentType, $Headers, $Body, $WebSession,
        [switch]$UseBasicParsing, $ErrorAction)
    if ($Headers.Origin -ne 'https://ooki-grader.test') { throw 'Mutation origin missing.' }
    $configurationRequests.Add([pscustomobject]@{ Uri=$Uri; Method=$Method; Headers=$Headers })
    return [pscustomobject]@{ StatusCode=200 }
}
function Invoke-RestMethod {
    param($Uri, $Method, $ContentType, $Headers, $Body, $WebSession)
    if ($Uri -eq 'https://ooki-grader.test/api/v1/auth/csrf') {
        return [pscustomobject]@{ csrfToken='synthetic-csrf-token' }
    }
    if ($Uri -ne 'https://ooki-grader.test/api/v1/admin/school-manager/' -or $Method -ne 'Put') {
        throw 'Unexpected HTTP operation.'
    }
    if ($Headers.Origin -ne 'https://ooki-grader.test' -or
        $Headers['X-CSRF-Token'] -ne 'synthetic-csrf-token') { throw 'Mutation guards missing.' }
    $key = [Guid]::Empty
    if (-not [Guid]::TryParse($Headers['Idempotency-Key'], [ref]$key)) {
        throw 'Valid idempotency key missing.'
    }
    $payload = [Text.Encoding]::UTF8.GetString($Body) | ConvertFrom-Json
    if ($payload.baseUrl -ne 'https://fsm.flens.jp/') { throw 'Destination changed.' }
    $configurationRequests.Add([pscustomobject]@{
        Uri=$Uri; Method=$Method; Headers=$Headers
        Enabled=$payload.enabled; DryRun=$payload.dryRun
    })
    return [pscustomobject]@{
        configured=$true; enabled=$payload.enabled; dryRun=$payload.dryRun
        baseUrl=$payload.baseUrl; username=$payload.username
    }
}
$fakePassword = ConvertTo-SecureString 'synthetic-test-password' -AsPlainText -Force
$parameters = @{
    OokiGraderUrl='https://ooki-grader.test'; AdminUsername='synthetic-admin'
    AdminPassword=$fakePassword; SchoolManagerUsername='synthetic-automation'
    SchoolManagerPassword=$fakePassword; Confirm=$false
}
$dry = & $configurationScript @parameters -Enable
$live = & $configurationScript @parameters -Enable -LiveSending
$off = & $configurationScript @parameters
if (-not $dry.enabled -or -not $dry.dryRun) { throw 'Default must be dry-run.' }
if (-not $live.enabled -or $live.dryRun) { throw 'Explicit live mode not applied.' }
if ($off.enabled -or -not $off.dryRun) { throw 'Disable must leave dry-run enabled.' }
$puts = @($configurationRequests | Where-Object Method -eq 'Put')
if ($puts.Count -ne 3 -or $configurationRequests.Count -ne 9) { throw 'Incomplete request lifecycle.' }
$keys = @($puts | ForEach-Object { $_.Headers['Idempotency-Key'] } | Select-Object -Unique)
if ($keys.Count -ne 3) { throw 'Separate mutations reused an idempotency key.' }
$before = $configurationRequests.Count
& $configurationScript @parameters -Enable -WhatIf | Out-Null
if ($configurationRequests.Count -ne $before) { throw 'WhatIf sent a request.' }
'PASS: dry-run, live, disable, Origin/CSRF/idempotency headers, logout and WhatIf; no network.'
