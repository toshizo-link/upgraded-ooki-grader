#requires -Version 7.4

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string] $FilePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'OokiGrader.Windows.psm1') -Force

Assert-OokiWindows
$target = Resolve-OokiExactPath -Path $FilePath `
    -Purpose 'File to Authenticode sign' -MustExist -PathType File
$thumbprint = $env:OOKI_SIGN_CERT_THUMBPRINT
if ([string]::IsNullOrWhiteSpace($thumbprint) -or
    $thumbprint -notmatch '^[A-Fa-f0-9]{40,128}$') {
    throw 'Set OOKI_SIGN_CERT_THUMBPRINT to the approved code-signing certificate thumbprint.'
}
$timestampText = $env:OOKI_SIGN_TIMESTAMP_URL
$timestampUri = $null
if ([string]::IsNullOrWhiteSpace($timestampText) -or
    -not [Uri]::TryCreate(
        $timestampText,
        [UriKind]::Absolute,
        [ref] $timestampUri) -or
    $timestampUri.Scheme -ne 'https') {
    throw 'Set OOKI_SIGN_TIMESTAMP_URL to the approved HTTPS RFC 3161 timestamp service.'
}

$kitsRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
$signTool = Get-ChildItem -LiteralPath $kitsRoot -Filter signtool.exe `
    -File -Recurse -ErrorAction Stop |
    Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
    Sort-Object {
        $candidateVersion = $null
        if ([version]::TryParse(
            $_.Directory.Parent.Name,
            [ref] $candidateVersion)) {
            $candidateVersion
        } else {
            [version] '0.0'
        }
    } -Descending |
    Select-Object -First 1
if ($null -eq $signTool) {
    throw 'The Windows SDK x64 signtool.exe was not found.'
}

if ($PSCmdlet.ShouldProcess($target, 'Apply SHA-256 Authenticode signature')) {
    Invoke-OokiNative -FilePath $signTool.FullName -ArgumentList @(
        'sign',
        '/fd',
        'SHA256',
        '/sha1',
        $thumbprint,
        '/tr',
        $timestampUri.AbsoluteUri,
        '/td',
        'SHA256',
        '/v',
        $target
    )
    Assert-OokiAuthenticodeSignature -FilePath $target `
        -ExpectedSignerThumbprint $thumbprint | Out-Null
}
