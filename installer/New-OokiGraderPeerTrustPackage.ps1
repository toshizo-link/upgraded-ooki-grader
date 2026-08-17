#requires -Version 7.4

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory)]
    [string] $CaCertificatePath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Fa-f0-9]{40,128}$')]
    [string] $ExpectedThumbprint,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$')]
    [string] $DnsName,

    [Parameter(Mandatory)]
    [string] $HostIpAddress,

    [Parameter(Mandatory)]
    [Uri] $Endpoint,

    [Parameter(Mandatory)]
    [string] $OutputRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'OokiGrader.Windows.psm1') -Force

Assert-OokiWindows
$caPath = Resolve-OokiExactPath -Path $CaCertificatePath `
    -Purpose 'CA public certificate' -MustExist -PathType File
$output = Resolve-OokiExactPath -Path $OutputRoot `
    -Purpose 'Peer trust package output root'
$parsedHostAddress = $null
if (-not [Net.IPAddress]::TryParse(
    $HostIpAddress,
    [ref] $parsedHostAddress) -or
    $parsedHostAddress.AddressFamily -ne `
        [Net.Sockets.AddressFamily]::InterNetwork) {
    throw 'The peer trust package requires one exact private IPv4 host address.'
}
Assert-OokiSchoolSubnet -SchoolSubnet @($HostIpAddress) | Out-Null
if ($Endpoint.Scheme -ne 'https') {
    throw 'The peer package endpoint must use HTTPS.'
}
if (-not $Endpoint.Host.Equals(
    $DnsName,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The peer package endpoint host must exactly match the certificate DNS name.'
}
if ($Endpoint.AbsolutePath -ne '/' -or
    -not [string]::IsNullOrWhiteSpace($Endpoint.Query) -or
    -not [string]::IsNullOrWhiteSpace($Endpoint.Fragment)) {
    throw 'The peer package endpoint must be the application origin with a root path and no query or fragment.'
}

$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $caPath)
try {
    $normalizedExpected = $ExpectedThumbprint.Replace(
        ' ', '').ToUpperInvariant()
    if (-not $certificate.Thumbprint.Equals(
        $normalizedExpected,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The CA certificate thumbprint does not match the expected value.'
    }
    $basicConstraints = $certificate.Extensions |
        Where-Object {
            $_ -is [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]
        } |
        Select-Object -First 1
    if ($null -eq $basicConstraints -or
        -not $basicConstraints.CertificateAuthority) {
        throw 'The peer trust package may contain only a public certificate-authority certificate.'
    }
    if ($certificate.HasPrivateKey) {
        throw 'The peer trust package must never contain a CA private key.'
    }
} finally {
    $certificate.Dispose()
}

function Get-OokiPeerPackageTextSha256 {
    param(
        [Parameter(Mandatory)]
        [string] $Value
    )

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Value)
        return ([BitConverter]::ToString(
            $algorithm.ComputeHash($bytes))).Replace(
                '-', '').ToLowerInvariant()
    } finally {
        $algorithm.Dispose()
    }
}

$trustScriptSource = Join-Path $PSScriptRoot `
    'Install-OokiGraderPeerTrust.ps1'
$moduleSource = Join-Path $PSScriptRoot 'OokiGrader.Windows.psm1'
foreach ($requiredSource in @($trustScriptSource, $moduleSource)) {
    if (-not [IO.File]::Exists($requiredSource)) {
        throw 'A required peer trust installer component is missing.'
    }
}
$trustScriptSha256 = (Get-FileHash -LiteralPath $trustScriptSource `
    -Algorithm SHA256).Hash.ToLowerInvariant()
$moduleSha256 = (Get-FileHash -LiteralPath $moduleSource `
    -Algorithm SHA256).Hash.ToLowerInvariant()
$installerSourceIdentity = @(
    "Install-OokiGraderPeerTrust.ps1=$trustScriptSha256",
    "OokiGrader.Windows.psm1=$moduleSha256"
) -join "`n"
$installerSourceSha256 = Get-OokiPeerPackageTextSha256 -Value (
    $installerSourceIdentity + "`n")

$canonicalEndpoint = $Endpoint.GetLeftPart([UriPartial]::Authority) + '/'
$packageIdentity = @(
    'schema=ooki-peer-trust-package-identity/v1',
    "dnsName=$($DnsName.ToLowerInvariant())",
    "hostIpAddress=$($parsedHostAddress.ToString())",
    "endpoint=$($canonicalEndpoint.ToLowerInvariant())",
    "caThumbprint=$normalizedExpected",
    "installerSourceSha256=$installerSourceSha256"
) -join "`n"
$packageIdentitySha256 = Get-OokiPeerPackageTextSha256 -Value (
    $packageIdentity + "`n")
$packageName = "OokiGrader-Client-Setup-$packageIdentitySha256"
$packagePath = Join-Path $output $packageName
if ([IO.File]::Exists($packagePath)) {
    throw 'The peer trust package target is occupied by a file.'
}
if ([IO.Directory]::Exists($packagePath)) {
    $existingInstaller = Join-Path $packagePath `
        'Install-OokiGraderPeerTrust.ps1'
    $existingMetadataPath = Join-Path $packagePath 'peer-trust.json'
    if (-not [IO.File]::Exists($existingInstaller) -or
        -not [IO.File]::Exists($existingMetadataPath)) {
        throw 'The existing immutable peer trust package is incomplete.'
    }
    $validationJson = & $existingInstaller -PackageMode `
        -WhatIf -Confirm:$false
    $validation = ($validationJson | Out-String) | ConvertFrom-Json
    $existingMetadata = Get-Content -LiteralPath $existingMetadataPath `
        -Raw | ConvertFrom-Json
    if ($validation.state -ne 'would-trust' -or
        -not ([string] $existingMetadata.caThumbprint).Equals(
            $normalizedExpected,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not ([string] $existingMetadata.dnsName).Equals(
            $DnsName,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not ([string] $existingMetadata.hostIpAddress).Equals(
            $HostIpAddress,
            [StringComparison]::Ordinal) -or
        -not ([string] $existingMetadata.endpoint).Equals(
            $canonicalEndpoint,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not ([string] $existingMetadata.installerSourceSha256).Equals(
            $installerSourceSha256,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not ([string] $existingMetadata.packageIdentitySha256).Equals(
            $packageIdentitySha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The existing immutable peer trust package does not match this installation.'
    }
    [pscustomobject]@{
        state = 'already-packaged'
        packagePath = $packagePath
        endpoint = $canonicalEndpoint
        readinessUri = [string] $existingMetadata.readinessUri
        caThumbprint = $normalizedExpected
        hostIpAddress = $HostIpAddress
        installerSourceSha256 = $installerSourceSha256
        packageIdentitySha256 = $packageIdentitySha256
        containsPrivateKey = $false
        classroomEntryPoint = 'Install-On-This-PC.cmd'
    } | ConvertTo-Json -Depth 6
    return
}

if (-not $PSCmdlet.ShouldProcess(
    $packagePath,
    'Create public-CA-only Ooki Grader classroom PC setup package')) {
    [pscustomobject]@{
        state = 'would-package'
        packagePath = $packagePath
        endpoint = $Endpoint.AbsoluteUri
        caThumbprint = $normalizedExpected
        installerSourceSha256 = $installerSourceSha256
        packageIdentitySha256 = $packageIdentitySha256
        containsPrivateKey = $false
    } | ConvertTo-Json -Depth 5
    return
}

[IO.Directory]::CreateDirectory($output) | Out-Null
$staging = Join-Path $output (
    ".staging-peer-trust-" + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($staging) | Out-Null
try {
    $packagedCaPath = Join-Path $staging 'ooki-grader-local-ca.cer'
    [IO.File]::Copy($caPath, $packagedCaPath, $false)
    [IO.File]::Copy(
        $trustScriptSource,
        (Join-Path $staging 'Install-OokiGraderPeerTrust.ps1'),
        $false)
    [IO.File]::Copy(
        $moduleSource,
        (Join-Path $staging 'OokiGrader.Windows.psm1'),
        $false)
    $packagedTrustScriptSha256 = (Get-FileHash -LiteralPath (
        Join-Path $staging 'Install-OokiGraderPeerTrust.ps1') `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    $packagedModuleSha256 = (Get-FileHash -LiteralPath (
        Join-Path $staging 'OokiGrader.Windows.psm1') `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $packagedTrustScriptSha256.Equals(
        $trustScriptSha256,
        [StringComparison]::OrdinalIgnoreCase) -or
        -not $packagedModuleSha256.Equals(
            $moduleSha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A peer trust installer source changed while the immutable package was being created.'
    }

    $readyUri = [Uri]::new(
        [Uri] $canonicalEndpoint,
        'health/ready').AbsoluteUri
    $metadata = [ordered]@{
        schema = 'ooki-peer-trust/v1'
        product = 'Ooki Grader'
        dnsName = $DnsName
        hostIpAddress = $HostIpAddress
        endpoint = $canonicalEndpoint
        readinessUri = $readyUri
        caCertificateFile = 'ooki-grader-local-ca.cer'
        caThumbprint = $normalizedExpected
        caCertificateSha256 = (
            Get-FileHash -LiteralPath $packagedCaPath `
                -Algorithm SHA256).Hash.ToLowerInvariant()
        installerSourceSha256 = $installerSourceSha256
        installerSources = [ordered]@{
            installPeerTrustSha256 = $packagedTrustScriptSha256
            windowsModuleSha256 = $packagedModuleSha256
        }
        packageIdentitySha256 = $packageIdentitySha256
        containsPrivateKey = $false
        hostsEntryManaged = $true
        createdAt = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $metadata | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath (Join-Path $staging 'peer-trust.json') `
            -Encoding UTF8

    $launcher = @'
@echo off
chcp 65001 >nul
net session >nul 2>&1
if errorlevel 1 (
  echo このファイルを右クリックし、「管理者として実行」を選んでください。
  pause
  exit /b 5
)
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File "%~dp0Install-OokiGraderPeerTrust.ps1" -PackageMode -CreateDesktopShortcut
if errorlevel 1 (
  echo.
  echo インストールまたは HTTPS 接続確認に失敗しました。表示された内容を技術担当者へお知らせください。
  pause
  exit /b 3
)
echo.
echo 信頼設定と HTTPS 接続確認が完了しました。デスクトップの Ooki Grader を開いてください。
pause
'@
    [IO.File]::WriteAllText(
        (Join-Path $staging 'Install-On-This-PC.cmd'),
        $launcher.Replace("`n", "`r`n"),
        [Text.UTF8Encoding]::new($false))

    $instructions = @"
Ooki Grader classroom PC setup

1. Copy this entire folder to the classroom PC.
2. Right-click Install-On-This-PC.cmd and select Run as administrator.
3. The installer trusts only the bundled public CA, writes the exact managed hosts entry,
   creates a desktop shortcut, and verifies the real HTTPS readiness endpoint.

Endpoint: $canonicalEndpoint
CA thumbprint: $normalizedExpected
Host address: $HostIpAddress

This folder contains no private key. Never bypass a browser certificate warning.
"@
    [IO.File]::WriteAllText(
        (Join-Path $staging 'README.txt'),
        $instructions.Replace("`n", "`r`n"),
        [Text.UTF8Encoding]::new($true))

    $checksumLines = Get-ChildItem -LiteralPath $staging -File |
        Sort-Object Name |
        ForEach-Object {
            $hash = (Get-FileHash -LiteralPath $_.FullName `
                -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $($_.Name)"
        }
    $checksumLines | Set-Content -LiteralPath (
        Join-Path $staging 'checksums.txt') -Encoding ASCII

    [IO.Directory]::Move($staging, $packagePath)
    [pscustomobject]@{
        state = 'packaged'
        packagePath = $packagePath
        endpoint = $canonicalEndpoint
        readinessUri = $readyUri
        caThumbprint = $normalizedExpected
        hostIpAddress = $HostIpAddress
        installerSourceSha256 = $installerSourceSha256
        packageIdentitySha256 = $packageIdentitySha256
        containsPrivateKey = $false
        classroomEntryPoint = 'Install-On-This-PC.cmd'
    } | ConvertTo-Json -Depth 6
} catch {
    if ([IO.Directory]::Exists($staging)) {
        [IO.Directory]::Delete($staging, $true)
    }
    throw
}
