[CmdletBinding(
    SupportsShouldProcess,
    ConfirmImpact = 'High',
    DefaultParameterSetName = 'External')]
param(
    [Parameter(Mandatory, ParameterSetName = 'External')]
    [ValidatePattern('^[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$')]
    [string] $PrimaryDnsName,

    [string[]] $AdditionalDnsName = @(),

    [string[]] $IpAddress = @(),

    [Parameter(Mandatory, ParameterSetName = 'External')]
    [string] $OutputDirectory,

    [string] $CaCertificateThumbprint,

    [switch] $CreateLocalCa,

    [switch] $AcknowledgeLocalCaPrivateKeyRisk,

    [switch] $Renew,

    [ValidateRange(30, 825)]
    [int] $HostValidityDays = 397,

    [ValidateRange(365, 3650)]
    [int] $CaValidityDays = 1825,

    [string] $ServiceName = 'OokiGrader.Host',

    [Parameter(Mandatory, ParameterSetName = 'WindowsPowerShellWorker',
        DontShow)]
    [switch] $WindowsPowerShellWorker,

    [Parameter(Mandatory, ParameterSetName = 'WindowsPowerShellWorker',
        DontShow)]
    [string] $WorkerRequestPath,

    [Parameter(Mandatory, ParameterSetName = 'WindowsPowerShellWorker',
        DontShow)]
    [string] $WorkerResponsePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'OokiGrader.Windows.psm1') -Force

Assert-OokiWindows
Assert-OokiAdministrator

function Write-CertificateResult {
    param(
        [Parameter(Mandatory)]
        [object] $Value
    )

    $json = $Value | ConvertTo-Json -Depth 6
    if ($WindowsPowerShellWorker) {
        [IO.File]::WriteAllText(
            $WorkerResponsePath,
            $json + "`r`n",
            [Text.UTF8Encoding]::new($false))
    } else {
        $json
    }
}

if ($WindowsPowerShellWorker) {
    if ($PSVersionTable.PSEdition -ne 'Desktop' -or
        $PSVersionTable.PSVersion.Major -ne 5) {
        throw 'The private certificate worker requires Windows PowerShell 5.1.'
    }

    $requestPath = Resolve-OokiExactPath -Path $WorkerRequestPath `
        -Purpose 'Certificate worker request' -MustExist -PathType File
    $responsePath = Resolve-OokiExactPath -Path $WorkerResponsePath `
        -Purpose 'Certificate worker response'
    $requestParent = [IO.Path]::GetDirectoryName($requestPath)
    $responseParent = [IO.Path]::GetDirectoryName($responsePath)
    if (-not $requestParent.Equals(
        $responseParent,
        [StringComparison]::OrdinalIgnoreCase) -or
        [IO.File]::Exists($responsePath)) {
        throw 'The certificate worker response must be a new file beside its request.'
    }
    $WorkerResponsePath = $responsePath

    try {
        $request = Get-Content -LiteralPath $requestPath -Raw |
            ConvertFrom-Json
    } catch {
        throw 'The certificate worker request is not valid JSON.'
    }
    if ($request.schema -ne 'ooki-certificate-worker/v1' -or
        $request.primaryDnsName -isnot [string] -or
        $request.outputDirectory -isnot [string] -or
        $request.serviceName -isnot [string] -or
        $request.createLocalCa -isnot [bool] -or
        $request.acknowledgeLocalCaPrivateKeyRisk -isnot [bool] -or
        $request.renew -isnot [bool]) {
        throw 'The certificate worker request has an unsupported shape.'
    }

    $PrimaryDnsName = [string] $request.primaryDnsName
    $AdditionalDnsName = [string[]] @($request.additionalDnsName)
    $IpAddress = [string[]] @($request.ipAddress)
    $OutputDirectory = [string] $request.outputDirectory
    $CaCertificateThumbprint = if (
        $null -eq $request.caCertificateThumbprint
    ) {
        ''
    } else {
        [string] $request.caCertificateThumbprint
    }
    $CreateLocalCa = [bool] $request.createLocalCa
    $AcknowledgeLocalCaPrivateKeyRisk =
        [bool] $request.acknowledgeLocalCaPrivateKeyRisk
    $Renew = [bool] $request.renew
    try {
        $HostValidityDays = [int] $request.hostValidityDays
        $CaValidityDays = [int] $request.caValidityDays
    } catch {
        throw 'The certificate worker validity periods must be integers.'
    }
    if ($HostValidityDays -lt 30 -or $HostValidityDays -gt 825 -or
        $CaValidityDays -lt 365 -or $CaValidityDays -gt 3650) {
        throw 'The certificate worker validity periods are outside the supported range.'
    }
    $ServiceName = [string] $request.serviceName

    # The outer PowerShell 7 process performs the user-facing ShouldProcess
    # decision. The worker must not prompt a second time in -NonInteractive mode.
    $ConfirmPreference = 'None'
} elseif ($PSVersionTable.PSEdition -eq 'Core') {
    if (-not $PSCmdlet.ShouldProcess(
        $PrimaryDnsName,
        'Run the complete live PKI operation in Windows PowerShell 5.1')) {
        return
    }

    $windowsPowerShell = Join-Path $env:SystemRoot `
        'System32\WindowsPowerShell\v1.0\powershell.exe'
    if (-not [IO.File]::Exists($windowsPowerShell)) {
        throw 'Windows PowerShell 5.1 is required for the Windows PKI certificate worker.'
    }

    $workerRoot = Join-Path ([IO.Path]::GetTempPath()) (
        'ooki-certificate-worker-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($workerRoot) | Out-Null
    $requestPath = Join-Path $workerRoot 'request.json'
    $responsePath = Join-Path $workerRoot 'response.json'
    try {
        $request = [ordered]@{
            schema = 'ooki-certificate-worker/v1'
            primaryDnsName = $PrimaryDnsName
            additionalDnsName = [string[]] @($AdditionalDnsName)
            ipAddress = [string[]] @($IpAddress)
            outputDirectory = $OutputDirectory
            caCertificateThumbprint = if (
                [string]::IsNullOrWhiteSpace($CaCertificateThumbprint)
            ) { $null } else { $CaCertificateThumbprint }
            createLocalCa = [bool] $CreateLocalCa
            acknowledgeLocalCaPrivateKeyRisk =
                [bool] $AcknowledgeLocalCaPrivateKeyRisk
            renew = [bool] $Renew
            hostValidityDays = [int] $HostValidityDays
            caValidityDays = [int] $CaValidityDays
            serviceName = $ServiceName
        }
        $requestJson = $request | ConvertTo-Json -Depth 4 -Compress
        [IO.File]::WriteAllText(
            $requestPath,
            $requestJson,
            [Text.UTF8Encoding]::new($false))

        # Bypass applies only to this already-running, verified script's private
        # worker. All PKI objects stay live inside this single Desktop process.
        $workerArguments = @(
            '-NoLogo',
            '-NoProfile',
            '-NonInteractive',
            '-ExecutionPolicy',
            'Bypass',
            '-File',
            $PSCommandPath,
            '-WindowsPowerShellWorker',
            '-WorkerRequestPath',
            $requestPath,
            '-WorkerResponsePath',
            $responsePath
        )
        $workerDiagnostics = & $windowsPowerShell @workerArguments 2>&1 |
            Out-String
        $workerExitCode = $LASTEXITCODE
        if ($workerExitCode -ne 0) {
            $detail = $workerDiagnostics.Trim()
            if ([string]::IsNullOrWhiteSpace($detail)) {
                $detail = 'No diagnostic output was returned.'
            }
            throw "Windows PowerShell certificate worker failed with exit code $workerExitCode. $detail"
        }
        if (-not [IO.File]::Exists($responsePath)) {
            throw 'Windows PowerShell certificate worker returned no response.'
        }
        try {
            $responseJson = [IO.File]::ReadAllText($responsePath)
            $response = $responseJson | ConvertFrom-Json
        } catch {
            throw 'Windows PowerShell certificate worker returned invalid JSON.'
        }
        if ($response.state -notin @('issued', 'renewed', 'already-current') -or
            $response.primaryDnsName -ne $PrimaryDnsName -or
            [string]::IsNullOrWhiteSpace(
                [string] $response.hostCertificatePath) -or
            [string]::IsNullOrWhiteSpace(
                [string] $response.caPublicCertificatePath)) {
            throw 'Windows PowerShell certificate worker returned an invalid result.'
        }
        $responseJson
        return
    } finally {
        if ([IO.Directory]::Exists($workerRoot)) {
            [IO.Directory]::Delete($workerRoot, $true)
        }
    }
}

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

    Write-CertificateResult -Value ([pscustomobject]@{
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
    })
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
            '*S-1-5-18:(OI)(CI)F',
            '*S-1-5-32-544:(OI)(CI)F'
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
    Write-CertificateResult -Value $metadata
}
