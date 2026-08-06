#requires -Version 7.4

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory)]
    [string] $PackageRoot,

    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$')]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $OutputRoot,

    [ValidatePattern('^[A-Fa-f0-9]{40,128}$')]
    [string] $ExpectedSignerThumbprint,

    [string] $InnoCompilerPath,

    [string] $SigningHook,

    [switch] $AllowUnsignedDevelopmentBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'OokiGrader.Windows.psm1') -Force

Assert-OokiWindows
if (-not $AllowUnsignedDevelopmentBuild -and
    [string]::IsNullOrWhiteSpace($ExpectedSignerThumbprint)) {
    throw 'A production installer requires the approved Authenticode signer thumbprint.'
}
if (-not $AllowUnsignedDevelopmentBuild -and
    [string]::IsNullOrWhiteSpace($SigningHook)) {
    throw 'A production installer requires an Authenticode signing hook for the final setup executable.'
}

$packageEvidence = Assert-OokiReleasePackage -PackageRoot $PackageRoot `
    -ExpectedVersion $Version `
    -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
    -AllowUnsignedDevelopmentBuild:$AllowUnsignedDevelopmentBuild
if (-not $AllowUnsignedDevelopmentBuild -and
    -not $packageEvidence.ProductionSigningClaimed) {
    throw 'The release inventory does not claim a completely signed production payload.'
}

$output = Resolve-OokiExactPath -Path $OutputRoot `
    -Purpose 'Windows installer output root'
$source = Join-Path $PSScriptRoot 'OokiGrader.Setup.iss'
if (-not [IO.File]::Exists($source)) {
    throw 'The checked-in Inno Setup source is missing.'
}

$compilerCandidate = $InnoCompilerPath
if ([string]::IsNullOrWhiteSpace($compilerCandidate)) {
    $compilerCandidate = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_) -and
        [IO.File]::Exists($_)
    } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($compilerCandidate)) {
    throw 'Inno Setup 6 ISCC.exe was not found. Install Inno Setup 6 or supply -InnoCompilerPath.'
}
$compiler = Resolve-OokiExactPath -Path $compilerCandidate `
    -Purpose 'Inno Setup compiler' -MustExist -PathType File
$compilerVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($compiler)
if ($compilerVersion.FileMajorPart -ne 6) {
    throw 'The Windows installer must be compiled with Inno Setup 6.'
}
$compilerSignature = Get-AuthenticodeSignature -LiteralPath $compiler
if (-not $AllowUnsignedDevelopmentBuild -and
    $compilerSignature.Status -ne 'Valid') {
    throw 'The Inno Setup compiler must have a valid trusted Authenticode signature.'
}

$signer = $null
if (-not [string]::IsNullOrWhiteSpace($SigningHook)) {
    $signer = Resolve-OokiExactPath -Path $SigningHook `
        -Purpose 'Installer Authenticode signing hook' -MustExist -PathType File
    if (-not [IO.Path]::GetExtension($signer).Equals(
        '.ps1',
        [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The Authenticode signing hook must be an explicit PowerShell script.'
    }
    Assert-OokiAuthenticodeSignature -FilePath $signer `
        -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
        -AllowUnsignedDevelopmentBuild:$AllowUnsignedDevelopmentBuild |
        Out-Null
}

$setupName = "OokiGrader-Setup-$Version-x64.exe"
$setupPath = Join-Path $output $setupName
$metadataPath = Join-Path $output (
    "OokiGrader-Setup-$Version-x64.json")
$checksumPath = Join-Path $output (
    "OokiGrader-Setup-$Version-x64.sha256")
foreach ($target in @($setupPath, $metadataPath, $checksumPath)) {
    if ([IO.File]::Exists($target)) {
        throw 'Windows installer outputs are immutable; the requested version already exists.'
    }
}

$numericVersion = ($Version -split '-', 2)[0] + '.0'
if ($PSCmdlet.ShouldProcess(
    $setupPath,
    'Compile, sign, and verify the Ooki Grader x64 Windows installer')) {
    [IO.Directory]::CreateDirectory($output) | Out-Null
    $arguments = @(
        '/Qp',
        "/DOokiPackageRoot=$($packageEvidence.Root)",
        "/DOokiVersion=$Version",
        "/DOokiNumericVersion=$numericVersion",
        "/DOokiOutputRoot=$output",
        "/DOokiExpectedSignerThumbprint=$ExpectedSignerThumbprint",
        "/DOokiAllowUnsigned=$(if ($AllowUnsignedDevelopmentBuild) { '1' } else { '0' })",
        "/DOokiSignOutput=$(if ($null -ne $signer) { '1' } else { '0' })",
        $source
    )
    if ($null -ne $signer) {
        $pwsh = Join-Path $PSHOME 'pwsh.exe'
        if (-not [IO.File]::Exists($pwsh)) {
            throw 'The Inno signing command requires the current 64-bit pwsh.exe.'
        }
        $signToolCommand = '"{0}" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy AllSigned -File "{1}" -FilePath $f' -f `
            $pwsh, $signer
        $arguments = @("/Sooki=$signToolCommand") + $arguments
    }
    Invoke-OokiNative -FilePath $compiler -ArgumentList $arguments
    if (-not [IO.File]::Exists($setupPath)) {
        throw 'Inno Setup completed without producing the expected setup executable.'
    }

    $setupSignature = Assert-OokiAuthenticodeSignature `
        -FilePath $setupPath `
        -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
        -AllowUnsignedDevelopmentBuild:$AllowUnsignedDevelopmentBuild
    $setupHash = (Get-FileHash -LiteralPath $setupPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    "$setupHash  $setupName" |
        Set-Content -LiteralPath $checksumPath -Encoding ASCII
    $metadata = [ordered]@{
        schema = 'ooki-windows-installer/v1'
        product = 'Ooki Grader'
        version = $Version
        runtime = 'win-x64'
        setupFile = $setupName
        setupSha256 = $setupHash
        setupSignature = $setupSignature.ExternalGate
        expectedSignerThumbprint = if ($AllowUnsignedDevelopmentBuild) {
            $null
        } else {
            $ExpectedSignerThumbprint.ToUpperInvariant()
        }
        packageFileCount = $packageEvidence.FileCount
        innoCompilerVersion = $compilerVersion.FileVersion
        innoCompilerSigner = if (
            $null -eq $compilerSignature.SignerCertificate
        ) { $null } else { $compilerSignature.SignerCertificate.Subject }
        selfContained = $true
        dataPreservedOnUninstall = $true
        upgradeMode = 'guarded-technician-script'
        builtAt = [DateTimeOffset]::UtcNow.ToString('O')
        externalGates = @(
            'Run clean install, reboot, same-version repair, guarded upgrade, and uninstall drills on Windows 11 Pro x64.',
            'Validate NTFS ACLs, service virtual-account startup, firewall scope, TLS trust, and DPAPI persistence on the target host.',
            'Publish the setup checksum independently through the controlled release channel.'
        )
    }
    $metadata | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $metadataPath -Encoding UTF8
    $metadata | ConvertTo-Json -Depth 8
}
