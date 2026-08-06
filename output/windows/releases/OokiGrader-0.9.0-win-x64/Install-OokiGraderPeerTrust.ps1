[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [string] $CaCertificatePath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Fa-f0-9]{40,128}$')]
    [string] $ExpectedThumbprint
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'OokiGrader.Windows.psm1') -Force

Assert-OokiWindows
Assert-OokiAdministrator
$certificatePath = Resolve-OokiExactPath -Path $CaCertificatePath `
    -Purpose 'CA public certificate' -MustExist -PathType File
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $certificatePath)
$normalizedExpected = $ExpectedThumbprint.Replace(' ', '').ToUpperInvariant()
if ($certificate.Thumbprint.ToUpperInvariant() -ne $normalizedExpected) {
    throw 'The CA certificate thumbprint does not match the independently supplied value.'
}
$basicConstraints = $certificate.Extensions |
    Where-Object {
        $_ -is [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]
    } |
    Select-Object -First 1
if ($null -eq $basicConstraints -or
    -not $basicConstraints.CertificateAuthority) {
    throw 'The selected certificate is not a certificate authority.'
}

$alreadyTrusted = Test-Path "Cert:\LocalMachine\Root\$normalizedExpected"
if (-not $alreadyTrusted -and
    $PSCmdlet.ShouldProcess(
        "LocalMachine Root $normalizedExpected",
        'Trust exact Ooki Grader CA certificate')) {
    Import-Certificate -FilePath $certificatePath `
        -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
}

[pscustomobject]@{
    state = if ($alreadyTrusted) {
        'already-trusted'
    } elseif ($WhatIfPreference) {
        'would-trust'
    } else {
        'trusted'
    }
    thumbprint = $normalizedExpected
    externalPeerValidationRequired = 'Open the HTTPS site by its canonical DNS name and confirm the browser reports no certificate warning.'
} | ConvertTo-Json -Depth 4
