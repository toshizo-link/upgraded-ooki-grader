[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$')]
    [string] $PrimaryDnsName,

    [string[]] $AdditionalDnsName = @(),

    [string[]] $IpAddress = @(),

    [Parameter(Mandatory)]
    [string] $OutputDirectory,

    [string] $CaCertificateThumbprint,

    [switch] $CreateLocalCa,

    [switch] $AcknowledgeLocalCaPrivateKeyRisk,

    [switch] $Renew,

    [ValidateRange(30, 825)]
    [int] $HostValidityDays = 397,

    [ValidateRange(365, 3650)]
    [int] $CaValidityDays = 1825,

    [string] $ServiceName = 'OokiGrader.Host'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'OokiGrader.Windows.psm1') -Force

Assert-OokiWindows
Assert-OokiAdministrator
$outputRoot = Resolve-OokiExactPath -Path $OutputDirectory `
    -Purpose 'Certificate output directory'
$dnsNames = @($PrimaryDnsName) + @($AdditionalDnsName) |
    Sort-Object -Unique
foreach ($dnsName in $dnsNames) {
    if ($dnsName -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$') {
        throw 'Every DNS SAN must be a valid host name.'
    }
}
foreach ($address in $IpAddress) {
    $parsed = $null
    if (-not [Net.IPAddress]::TryParse($address, [ref] $parsed)) {
        throw 'Every IP SAN must be a valid IPv4 or IPv6 address.'
    }
}
$metadataPath = Join-Path $outputRoot 'certificate-metadata.json'
$existingMetadata = $null
if ([IO.File]::Exists($metadataPath)) {
    $existingMetadata = Get-Content -LiteralPath $metadataPath -Raw |
        ConvertFrom-Json
}
if ($null -ne $existingMetadata -and -not $Renew) {
    $expectedDns = @($dnsNames | Sort-Object)
    $actualDns = @($existingMetadata.dnsSans | Sort-Object)
    $expectedIp = @($IpAddress | Sort-Object)
    $actualIp = @($existingMetadata.ipSans | Sort-Object)
    $metadataMatches = $existingMetadata.primaryDnsName -eq
        $PrimaryDnsName -and
        ($expectedDns -join "`n") -ceq ($actualDns -join "`n") -and
        ($expectedIp -join "`n") -ceq ($actualIp -join "`n") -and
        [IO.File]::Exists($existingMetadata.hostCertificatePath) -and
        [IO.File]::Exists($existingMetadata.caPublicCertificatePath)
    if (-not $metadataMatches) {
        throw 'Existing certificate metadata differs from the requested SANs. Use -Renew to issue a new versioned certificate.'
    }

    $existingHost = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $existingMetadata.hostCertificatePath,
        '',
        [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    try {
        if ($existingHost.Thumbprint -ne $existingMetadata.hostThumbprint -or
            $existingHost.GetNameInfo(
                [Security.Cryptography.X509Certificates.X509NameType]::DnsName,
                $false) -ne $PrimaryDnsName) {
            throw 'The existing host certificate does not match its protected metadata. Use -Renew.'
        }
        if ($existingHost.NotAfter.ToUniversalTime() -le
            [DateTimeOffset]::UtcNow.AddDays(30).UtcDateTime) {
            throw 'The existing host certificate is near expiry. Use -Renew.'
        }
    } finally {
        $existingHost.Dispose()
    }
    $existingCaThumbprint = ([string] $existingMetadata.caThumbprint).Replace(
        ' ',
        '').ToUpperInvariant()
    $hostTrustPath = "Cert:\LocalMachine\Root\$existingCaThumbprint"
    if (-not (Test-Path $hostTrustPath) -and
        $PSCmdlet.ShouldProcess(
            $hostTrustPath,
            'Install existing Ooki Grader CA into host trust store')) {
        Import-Certificate `
            -FilePath ([string] $existingMetadata.caPublicCertificatePath) `
            -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
    }

    [pscustomobject]@{
        state = 'already-current'
        primaryDnsName = $existingMetadata.primaryDnsName
        hostCertificatePath = $existingMetadata.hostCertificatePath
        hostThumbprint = $existingMetadata.hostThumbprint
        caPublicCertificatePath = $existingMetadata.caPublicCertificatePath
        caThumbprint = $existingMetadata.caThumbprint
        hostTrustInstalled = (Test-Path $hostTrustPath)
        dnsSans = $actualDns
        ipSans = $actualIp
        peerTrustExternalGate = $existingMetadata.peerTrustExternalGate
        codeSigningExternalGate = $existingMetadata.codeSigningExternalGate
    } | ConvertTo-Json -Depth 6
    return
}

$ca = $null
if (-not [string]::IsNullOrWhiteSpace($CaCertificateThumbprint)) {
    $normalizedThumbprint = $CaCertificateThumbprint.Replace(' ', '')
    $ca = Get-Item "Cert:\LocalMachine\My\$normalizedThumbprint" `
        -ErrorAction Stop
    if (-not $ca.HasPrivateKey) {
        throw 'The selected issuing CA certificate has no accessible private key.'
    }
} elseif (
    $Renew -and
    $null -ne $existingMetadata -and
    -not [string]::IsNullOrWhiteSpace($existingMetadata.caThumbprint)
) {
    $previousCaThumbprint = $existingMetadata.caThumbprint.Replace(' ', '')
    $ca = Get-Item "Cert:\LocalMachine\My\$previousCaThumbprint" `
        -ErrorAction Stop
    if (-not $ca.HasPrivateKey) {
        throw 'The prior issuing CA private key is unavailable. Supply another CA explicitly.'
    }
} elseif ($CreateLocalCa) {
    if (-not $AcknowledgeLocalCaPrivateKeyRisk) {
        throw 'Creating a host-local CA requires -AcknowledgeLocalCaPrivateKeyRisk. The non-exportable CA key remains protected on this Windows host; losing the host requires re-trusting a new CA.'
    }
    if ($PSCmdlet.ShouldProcess(
        'LocalMachine certificate store',
        'Create Ooki Grader local CA')) {
        $ca = New-SelfSignedCertificate -Type Custom `
            -Subject 'CN=Ooki Grader Local CA' `
            -FriendlyName 'Ooki Grader Local CA' `
            -CertStoreLocation 'Cert:\LocalMachine\My' `
            -KeyAlgorithm RSA -KeyLength 4096 -HashAlgorithm SHA256 `
            -KeyExportPolicy NonExportable `
            -KeyUsage CertSign, CRLSign, DigitalSignature `
            -NotAfter ([DateTimeOffset]::UtcNow.AddDays(
                $CaValidityDays).UtcDateTime) `
            -TextExtension @(
                '2.5.29.19={critical}{text}ca=1&pathlength=0'
            )
    }
} else {
    throw 'Supply an issuing CA thumbprint, or explicitly create a local CA.'
}

if ($null -eq $ca) {
    return
}

$sanItems = @($dnsNames | ForEach-Object { "DNS=$_" })
$sanItems += @($IpAddress | ForEach-Object { "IPAddress=$_" })
$sanExtension = '2.5.29.17={text}' + ($sanItems -join '&')
$hostCertificate = $null
if ($PSCmdlet.ShouldProcess(
    $PrimaryDnsName,
    'Issue HTTPS host certificate with exact DNS/IP SANs')) {
    $hostCertificate = New-SelfSignedCertificate -Type Custom `
        -Subject "CN=$PrimaryDnsName" `
        -FriendlyName "Ooki Grader HTTPS - $PrimaryDnsName" `
        -CertStoreLocation 'Cert:\LocalMachine\My' `
        -Signer $ca -KeyAlgorithm RSA -KeyLength 3072 `
        -HashAlgorithm SHA256 -KeyExportPolicy Exportable `
        -KeyUsage DigitalSignature, KeyEncipherment `
        -NotAfter ([DateTimeOffset]::UtcNow.AddDays(
            $HostValidityDays).UtcDateTime) `
        -TextExtension @(
            $sanExtension,
            '2.5.29.37={text}1.3.6.1.5.5.7.3.1'
        )
}

if ($null -eq $hostCertificate) {
    return
}

if ($PSCmdlet.ShouldProcess(
    $outputRoot,
    'Export restricted host PFX and public CA certificate')) {
    [IO.Directory]::CreateDirectory($outputRoot) | Out-Null
    Invoke-OokiNative -FilePath "$env:SystemRoot\System32\icacls.exe" `
        -ArgumentList @(
            $outputRoot,
            '/inheritance:r',
            '/grant:r',
            'SYSTEM:(OI)(CI)F',
            'BUILTIN\Administrators:(OI)(CI)F'
        )
    $emptyPassword = [Security.SecureString]::new()
    $pfxPath = Join-Path $outputRoot (
        'ooki-grader-host-' +
        $hostCertificate.Thumbprint.ToLowerInvariant() +
        '.pfx')
    $caPath = Join-Path $outputRoot (
        'ooki-grader-local-ca-' +
        $ca.Thumbprint.ToLowerInvariant() +
        '.cer')
    Export-PfxCertificate -Cert $hostCertificate -FilePath $pfxPath `
        -Password $emptyPassword -NoProperties | Out-Null
    if (-not [IO.File]::Exists($caPath)) {
        Export-Certificate -Cert $ca -FilePath $caPath -Type CERT |
            Out-Null
    }
    $caThumbprint = $ca.Thumbprint.Replace(' ', '').ToUpperInvariant()
    $hostAlreadyTrustsCa = Test-Path "Cert:\LocalMachine\Root\$caThumbprint"
    if (-not $hostAlreadyTrustsCa) {
        Import-Certificate -FilePath $caPath `
            -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
    }
    # The issuance folder is restricted to SYSTEM and Administrators. The
    # service receives read access only after its virtual account exists and
    # Install-OokiGrader copies the PFX into the managed certificate path.
    if ($null -ne (Get-Service -Name $ServiceName `
        -ErrorAction SilentlyContinue)) {
        Set-OokiCertificateAcl -CertificatePath $pfxPath `
            -ServiceName $ServiceName -Confirm:$false
    }
    $metadata = [ordered]@{
        state = if ($Renew) { 'renewed' } else { 'issued' }
        primaryDnsName = $PrimaryDnsName
        hostCertificatePath = $pfxPath
        hostThumbprint = $hostCertificate.Thumbprint
        caPublicCertificatePath = $caPath
        caThumbprint = $ca.Thumbprint
        hostTrustInstalled = $true
        caPrivateKeyExportable = $false
        caPrivateKeyLocation = 'LocalMachine certificate store on the Ooki Grader host only'
        dnsSans = $dnsNames
        ipSans = $IpAddress
        peerTrustExternalGate = 'Install only the exported public CA on authorized peers, then validate DNS and TLS without bypasses.'
        codeSigningExternalGate = 'This TLS CA does not sign application binaries; release Authenticode signing remains separate.'
    }
    Write-OokiJsonFile -Path $metadataPath -Value $metadata `
        -Confirm:$false
    $metadata | ConvertTo-Json -Depth 6
}
