[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^https://')]
    [string] $OokiGraderUrl,

    [Parameter(Mandatory)]
    [ValidateLength(1, 200)]
    [string] $AdminUsername,

    [Security.SecureString] $AdminPassword,

    [Parameter(Mandatory)]
    [ValidateLength(1, 200)]
    [string] $SchoolManagerUsername,

    [Security.SecureString] $SchoolManagerPassword,

    [switch] $Enable,

    [switch] $LiveSending
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($LiveSending -and -not $Enable) {
    throw 'LiveSending requires Enable.'
}

$baseUri = [Uri] $OokiGraderUrl
if ($baseUri.Scheme -ne 'https' -or -not $baseUri.IsAbsoluteUri) {
    throw 'OokiGraderUrl must be an absolute HTTPS URL.'
}
$target = $baseUri.AbsoluteUri.TrimEnd('/')
$mode = if (-not $Enable) {
    'disabled'
} elseif ($LiveSending) {
    'enabled for live guardian delivery'
} else {
    'enabled in dry-run mode'
}
if (-not $PSCmdlet.ShouldProcess(
        $target,
        "Configure School Manager automation ($mode)")) {
    return
}

if ($null -eq $AdminPassword) {
    $AdminPassword = Read-Host 'Ooki Grader 管理者パスワード' -AsSecureString
}
if ($null -eq $SchoolManagerPassword) {
    $SchoolManagerPassword = Read-Host 'School Manager 自動化パスワード' `
        -AsSecureString
}

function ConvertFrom-OokiSecureString {
    param(
        [Parameter(Mandatory)]
        [Security.SecureString] $Value
    )

    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    } finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

$adminPlaintext = $null
$schoolManagerPlaintext = $null
$csrfToken = $null
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
try {
    $adminPlaintext = ConvertFrom-OokiSecureString -Value $AdminPassword
    $loginJson = @{
        username = $AdminUsername
        password = $adminPlaintext
    } | ConvertTo-Json -Compress
    Invoke-WebRequest -Uri "$target/api/v1/auth/login" `
        -Method Post -ContentType 'application/json; charset=utf-8' `
        -Body ([Text.Encoding]::UTF8.GetBytes($loginJson)) `
        -WebSession $session -UseBasicParsing | Out-Null

    $csrf = Invoke-RestMethod -Uri "$target/api/v1/auth/csrf" `
        -Method Get -WebSession $session
    if ([string]::IsNullOrWhiteSpace([string] $csrf.csrfToken)) {
        throw 'Ooki Grader did not return a CSRF token.'
    }
    $csrfToken = [string] $csrf.csrfToken

    $schoolManagerPlaintext = ConvertFrom-OokiSecureString `
        -Value $SchoolManagerPassword
    $settingsJson = @{
        baseUrl = 'https://fsm.flens.jp/'
        username = $SchoolManagerUsername
        password = $schoolManagerPlaintext
        enabled = [bool] $Enable
        dryRun = -not [bool] $LiveSending
    } | ConvertTo-Json -Compress
    $result = Invoke-RestMethod `
        -Uri "$target/api/v1/admin/school-manager/" `
        -Method Put `
        -ContentType 'application/json; charset=utf-8' `
        -Headers @{ 'X-CSRF-Token' = $csrfToken } `
        -Body ([Text.Encoding]::UTF8.GetBytes($settingsJson)) `
        -WebSession $session
    [pscustomobject]@{
        state = 'configured'
        enabled = [bool] $result.enabled
        dryRun = [bool] $result.dryRun
        baseUrl = [string] $result.baseUrl
        username = [string] $result.username
        credentialsProtectedByService = [bool] $result.configured
    }
} finally {
    if ($null -ne $adminPlaintext) {
        $adminPlaintext = $null
    }
    if ($null -ne $schoolManagerPlaintext) {
        $schoolManagerPlaintext = $null
    }
    try {
        if (-not [string]::IsNullOrWhiteSpace($csrfToken)) {
            Invoke-WebRequest -Uri "$target/api/v1/auth/logout" `
                -Method Post -WebSession $session -UseBasicParsing `
                -Headers @{ 'X-CSRF-Token' = $csrfToken } `
                -ErrorAction SilentlyContinue | Out-Null
        }
    } catch {
        # Session expiry is bounded by the Ooki Grader server even if logout
        # cannot be completed after configuration.
    }
}
