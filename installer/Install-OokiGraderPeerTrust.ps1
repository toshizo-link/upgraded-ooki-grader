[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [string] $CaCertificatePath,

    [ValidatePattern('^[A-Fa-f0-9]{40,128}$')]
    [string] $ExpectedThumbprint,

    [switch] $PackageMode,

    [switch] $CreateDesktopShortcut
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'OokiGrader.Windows.psm1') -Force

Assert-OokiWindows
Assert-OokiAdministrator
$metadata = $null
$endpoint = $null
$dnsName = $null
$hostIpAddress = $null

if ($PackageMode) {
    if (-not [string]::IsNullOrWhiteSpace($CaCertificatePath) -or
        -not [string]::IsNullOrWhiteSpace($ExpectedThumbprint)) {
        throw 'Package mode obtains its fixed CA identity from peer-trust.json; do not override it on the command line.'
    }
    $checksumPath = Join-Path $PSScriptRoot 'checksums.txt'
    if (-not [IO.File]::Exists($checksumPath)) {
        throw 'The peer trust package checksum manifest is missing.'
    }
    $expectedFiles = @{}
    foreach ($line in Get-Content -LiteralPath $checksumPath) {
        if ($line -notmatch '^([A-Fa-f0-9]{64})  ([^\\/\r\n]+)$' -or
            $Matches[2] -in @('.', '..') -or
            $expectedFiles.ContainsKey($Matches[2])) {
            throw 'The peer trust package checksum manifest is invalid.'
        }
        $expectedFiles[$Matches[2]] = $Matches[1]
    }
    $actualFiles = @(Get-ChildItem -LiteralPath $PSScriptRoot -File |
        Where-Object Name -ne 'checksums.txt')
    if ($expectedFiles.Count -ne $actualFiles.Count) {
        throw 'The peer trust package contains a missing or unexpected file.'
    }
    foreach ($file in $actualFiles) {
        if (-not $expectedFiles.ContainsKey($file.Name) -or
            -not (Get-FileHash -LiteralPath $file.FullName `
                -Algorithm SHA256).Hash.Equals(
                    [string] $expectedFiles[$file.Name],
                    [StringComparison]::OrdinalIgnoreCase)) {
            throw "The peer trust package failed its checksum for $($file.Name)."
        }
    }

    $metadataPath = Join-Path $PSScriptRoot 'peer-trust.json'
    if (-not [IO.File]::Exists($metadataPath)) {
        throw 'The peer trust package metadata is missing.'
    }
    try {
        $metadata = Get-Content -LiteralPath $metadataPath -Raw |
            ConvertFrom-Json
    } catch {
        throw 'The peer trust package metadata is not valid JSON.'
    }
    if ($metadata.schema -ne 'ooki-peer-trust/v1' -or
        $metadata.product -ne 'Ooki Grader' -or
        $metadata.caCertificateFile -ne 'ooki-grader-local-ca.cer' -or
        [bool] $metadata.containsPrivateKey -or
        -not [bool] $metadata.hostsEntryManaged) {
        throw 'The peer trust package metadata has an unsupported or unsafe shape.'
    }
    if (@(Get-ChildItem -LiteralPath $PSScriptRoot -File |
        Where-Object Extension -in @('.pfx', '.p12')).Count -ne 0) {
        throw 'The peer trust package unexpectedly contains a private-key file.'
    }
    $CaCertificatePath = Join-Path $PSScriptRoot `
        ([string] $metadata.caCertificateFile)
    $ExpectedThumbprint = [string] $metadata.caThumbprint
    if ($ExpectedThumbprint -notmatch '^[A-Fa-f0-9]{40,128}$') {
        throw 'The packaged CA thumbprint is invalid.'
    }
    $expectedCaHash = [string] $metadata.caCertificateSha256
    if ($expectedCaHash -notmatch '^[A-Fa-f0-9]{64}$' -or
        -not [IO.File]::Exists($CaCertificatePath) -or
        -not (Get-FileHash -LiteralPath $CaCertificatePath `
            -Algorithm SHA256).Hash.Equals(
                $expectedCaHash,
                [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The packaged CA certificate failed its SHA-256 integrity check.'
    }
    $dnsName = [string] $metadata.dnsName
    if ($dnsName -notmatch `
        '^[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$') {
        throw 'The packaged DNS name is invalid.'
    }
    $hostIpAddress = [string] $metadata.hostIpAddress
    $parsedHostAddress = $null
    if (-not [Net.IPAddress]::TryParse(
        $hostIpAddress,
        [ref] $parsedHostAddress) -or
        $parsedHostAddress.AddressFamily -ne `
            [Net.Sockets.AddressFamily]::InterNetwork) {
        throw 'The packaged host address must be one exact private IPv4 address.'
    }
    Assert-OokiSchoolSubnet -SchoolSubnet @($hostIpAddress) | Out-Null
    try {
        $endpoint = [Uri] ([string] $metadata.endpoint)
    } catch {
        throw 'The packaged application endpoint is invalid.'
    }
    if ($endpoint.Scheme -ne 'https' -or
        -not $endpoint.Host.Equals(
            $dnsName,
            [StringComparison]::OrdinalIgnoreCase) -or
        $endpoint.AbsolutePath -ne '/' -or
        -not [string]::IsNullOrWhiteSpace($endpoint.Query) -or
        -not [string]::IsNullOrWhiteSpace($endpoint.Fragment)) {
        throw 'The packaged application endpoint must be the exact HTTPS origin for the certificate DNS name.'
    }
} elseif ([string]::IsNullOrWhiteSpace($CaCertificatePath) -or
    [string]::IsNullOrWhiteSpace($ExpectedThumbprint)) {
    throw 'Supply both -CaCertificatePath and -ExpectedThumbprint, or use the immutable generated package with -PackageMode.'
}

$certificatePath = Resolve-OokiExactPath -Path $CaCertificatePath `
    -Purpose 'CA public certificate' -MustExist -PathType File
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $certificatePath)
$normalizedExpected = $ExpectedThumbprint.Replace(' ', '').ToUpperInvariant()
try {
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
    if ($certificate.HasPrivateKey) {
        throw 'Peer computers may receive only the public CA certificate, never its private key.'
    }
} finally {
    $certificate.Dispose()
}

$hostsResult = $null
if ($PackageMode) {
    $hostsResult = Set-OokiManagedHostsEntry -DnsName $dnsName `
        -IpAddress $hostIpAddress -Confirm:$false `
        -WhatIf:$WhatIfPreference
}

$trustPath = "Cert:\LocalMachine\Root\$normalizedExpected"
$alreadyTrusted = Test-Path $trustPath
if (-not $alreadyTrusted -and
    $PSCmdlet.ShouldProcess(
        "LocalMachine Root $normalizedExpected",
        'Trust exact Ooki Grader CA certificate')) {
    Import-Certificate -FilePath $certificatePath `
        -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
}

$httpsReady = $null
if ($PackageMode -and -not $WhatIfPreference) {
    $readyUri = [Uri]::new($endpoint, 'health/ready')
    $httpsReady = Test-OokiReadyEndpoint -Uri $readyUri `
        -TimeoutSeconds 45
    if (-not $httpsReady) {
        throw 'The CA and hosts entry were installed, but the real HTTPS readiness check failed. Do not bypass a browser warning; verify the host service, fixed IP, firewall scope, and PC network.'
    }
}

$shortcutPath = $null
if ($CreateDesktopShortcut -and $PackageMode -and
    ($httpsReady -or $WhatIfPreference)) {
    $commonDesktop = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::CommonDesktopDirectory)
    $shortcutPath = Join-Path $commonDesktop 'Ooki Grader.url'
    if ($PSCmdlet.ShouldProcess(
        $shortcutPath,
        'Create Ooki Grader HTTPS desktop shortcut')) {
        $shortcut = @(
            '[InternetShortcut]',
            "URL=$($endpoint.AbsoluteUri)",
            'IconIndex=0'
        ) -join "`r`n"
        [IO.File]::WriteAllText(
            $shortcutPath,
            $shortcut + "`r`n",
            [Text.UTF8Encoding]::new($true))
    }
}

[pscustomobject]@{
    state = if ($WhatIfPreference) {
        'would-trust'
    } elseif ($alreadyTrusted) {
        'already-trusted'
    } else {
        'trusted'
    }
    thumbprint = $normalizedExpected
    packageMode = [bool] $PackageMode
    hostsEntry = $hostsResult
    endpoint = if ($null -eq $endpoint) { $null } else {
        $endpoint.AbsoluteUri
    }
    httpsReady = $httpsReady
    tlsBypassUsed = $false
    shortcutPath = $shortcutPath
    externalPeerValidationRequired = if ($PackageMode) {
        'Open the generated desktop shortcut and confirm the browser reports no certificate warning.'
    } else {
        'Open the HTTPS site by its canonical DNS name and confirm the browser reports no certificate warning.'
    }
} | ConvertTo-Json -Depth 7
