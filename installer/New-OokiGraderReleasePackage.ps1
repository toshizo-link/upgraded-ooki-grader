#requires -Version 7.4

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$')]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $OutputRoot,

    [string] $SourceRoot = (Split-Path -Parent $PSScriptRoot),

    [ValidateSet('Release')]
    [string] $Configuration = 'Release',

    [ValidateSet('win-x64')]
    [string] $Runtime = 'win-x64',

    [string] $SigningHook
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'OokiGrader.Windows.psm1') -Force

function Invoke-CheckedDotNet {
    param(
        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code $LASTEXITCODE."
    }
}

function Copy-PublishTree {
    param(
        [Parameter(Mandatory)]
        [string] $Source,

        [Parameter(Mandatory)]
        [string] $Destination
    )

    foreach ($file in Get-ChildItem -LiteralPath $Source -File -Recurse |
        Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($Source, $file.FullName)
        $target = Join-Path $Destination $relative
        $targetParent = Split-Path -Parent $target
        [IO.Directory]::CreateDirectory($targetParent) | Out-Null
        if ([IO.File]::Exists($target)) {
            $sourceHash = (Get-FileHash -LiteralPath $file.FullName `
                -Algorithm SHA256).Hash
            $targetHash = (Get-FileHash -LiteralPath $target `
                -Algorithm SHA256).Hash
            if (-not $sourceHash.Equals(
                $targetHash,
                [StringComparison]::OrdinalIgnoreCase)) {
                throw "Publish outputs contain a conflicting file: $relative"
            }
            continue
        }
        [IO.File]::Copy($file.FullName, $target, $false)
    }
}

$source = Resolve-OokiExactPath -Path $SourceRoot `
    -Purpose 'Repository source root' -MustExist -PathType Directory
$output = Resolve-OokiExactPath -Path $OutputRoot `
    -Purpose 'Release output root'
$hostProject = Join-Path $source `
    'src/OokiGrader.Host/OokiGrader.Host.csproj'
$toolProject = Join-Path $source `
    'src/OokiGrader.Tool/OokiGrader.Tool.csproj'
if (-not [IO.File]::Exists($hostProject) -or
    -not [IO.File]::Exists($toolProject)) {
    throw 'The Host and Tool projects are required for release packaging.'
}
$packageName = "OokiGrader-$Version-$Runtime"
$packageRoot = Join-Path $output $packageName
if ([IO.Directory]::Exists($packageRoot) -or
    [IO.File]::Exists($packageRoot)) {
    throw 'The release package target already exists; packages are immutable.'
}

$signer = $null
if (-not [string]::IsNullOrWhiteSpace($SigningHook)) {
    $signer = Resolve-OokiExactPath -Path $SigningHook `
        -Purpose 'Authenticode signing hook' -MustExist -PathType File
    if (-not [IO.Path]::GetExtension($signer).Equals(
        '.ps1',
        [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The optional signing hook must be an explicit PowerShell script.'
    }
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'Authenticode signing hooks are supported only on Windows.'
    }
}

if ($PSCmdlet.ShouldProcess(
    $packageRoot,
    'Publish Host and Tool and assemble immutable Windows release package')) {
    [IO.Directory]::CreateDirectory($output) | Out-Null
    $staging = Join-Path $output (
        ".staging-$packageName-" + [Guid]::NewGuid().ToString('N'))
    $hostPublish = Join-Path $staging 'host'
    $toolPublish = Join-Path $staging 'tool'
    $payload = Join-Path $staging 'payload'
    [IO.Directory]::CreateDirectory($hostPublish) | Out-Null
    [IO.Directory]::CreateDirectory($toolPublish) | Out-Null
    [IO.Directory]::CreateDirectory($payload) | Out-Null
    try {
        Invoke-CheckedDotNet -Arguments @(
            'restore',
            $hostProject,
            '--runtime',
            $Runtime,
            '--disable-build-servers'
        )
        Invoke-CheckedDotNet -Arguments @(
            'restore',
            $toolProject,
            '--runtime',
            $Runtime,
            '--disable-build-servers'
        )
        $commonPublish = @(
            '--configuration', $Configuration,
            '--runtime', $Runtime,
            '--self-contained', 'true',
            '--no-restore',
            '-p:ContinuousIntegrationBuild=true',
            '-p:DebugSymbols=false',
            '-p:DebugType=None',
            "-p:Version=$Version",
            "-p:InformationalVersion=$Version"
        )
        Invoke-CheckedDotNet -Arguments (
            @(
                'publish', $hostProject,
                '--output', $hostPublish,
                '-p:PublishSingleFile=false'
            ) + $commonPublish)
        Invoke-CheckedDotNet -Arguments (
            @(
                'publish', $toolProject,
                '--output', $toolPublish,
                '-p:PublishSingleFile=true',
                '-p:IncludeNativeLibrariesForSelfExtract=true',
                '-p:EnableCompressionInSingleFile=true'
            ) + $commonPublish)

        Copy-PublishTree -Source $hostPublish -Destination $payload
        Copy-PublishTree -Source $toolPublish -Destination $payload
        foreach ($requiredExecutable in @(
            'OokiGrader.Host.exe',
            'OokiGrader.Tool.exe'
        )) {
            if (-not [IO.File]::Exists(
                (Join-Path $payload $requiredExecutable))) {
                throw "The publish output is missing $requiredExecutable."
            }
        }

        $technicianFiles = @(
            'Install-OokiGrader.ps1',
            'Upgrade-OokiGrader.ps1',
            'Repair-OokiGrader.ps1',
            'Restore-OokiGrader.ps1',
            'Uninstall-OokiGrader.ps1',
            'Test-OokiGraderHealth.ps1',
            'Test-OokiGraderPreflight.ps1',
            'New-OokiGraderCertificate.ps1',
            'Install-OokiGraderPeerTrust.ps1',
            'OokiGrader.Windows.psm1'
        )
        foreach ($name in $technicianFiles) {
            $installerSource = Join-Path (
                Join-Path $source 'installer') $name
            if (-not [IO.File]::Exists($installerSource)) {
                throw "The technician package is missing $name."
            }
            [IO.File]::Copy(
                $installerSource,
                (Join-Path $payload $name),
                $false)
        }

        $signingHookState = 'not-requested'
        $signerThumbprint = $null
        if ($null -ne $signer) {
            $signableFiles = @(
                Get-ChildItem -LiteralPath $payload -File -Recurse |
                    Where-Object {
                        $_.Extension -in @(
                            '.exe',
                            '.dll',
                            '.ps1',
                            '.psm1'
                        )
                    } |
                    Sort-Object FullName
            )
            foreach ($signableFile in $signableFiles) {
                & $signer -FilePath $signableFile.FullName
                if (-not $?) {
                    throw "The signing hook failed for $($signableFile.Name)."
                }
                $signature = Get-AuthenticodeSignature `
                    -LiteralPath $signableFile.FullName
                if ($signature.Status -ne 'Valid') {
                    throw "The signing hook did not produce a valid Authenticode signature for $($signableFile.Name)."
                }
                $currentThumbprint =
                    $signature.SignerCertificate.Thumbprint.Replace(' ', '')
                if ($null -eq $signerThumbprint) {
                    $signerThumbprint = $currentThumbprint
                } elseif (-not $signerThumbprint.Equals(
                    $currentThumbprint,
                    [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'The signing hook used more than one publisher certificate.'
                }
            }
            $signingHookState =
                'hook-output-verified-valid-on-build-host'
        }

        $artifacts = @(
            Get-ChildItem -LiteralPath $payload -File -Recurse |
                Sort-Object {
                    [IO.Path]::GetRelativePath($payload, $_.FullName)
                } |
                ForEach-Object {
                    $relative = [IO.Path]::GetRelativePath(
                        $payload,
                        $_.FullName).Replace('\', '/')
                    [ordered]@{
                        path = $relative
                        bytes = $_.Length
                        sha256 = (
                            Get-FileHash -LiteralPath $_.FullName `
                                -Algorithm SHA256).Hash.ToLowerInvariant()
                        kind = if ($_.Extension -eq '.exe') {
                            'windows-executable'
                        } elseif ($_.Extension -in @('.dll', '.json')) {
                            'runtime-component'
                        } elseif ($_.Extension -in @('.ps1', '.psm1')) {
                            'technician-script'
                        } else {
                            'application-asset'
                        }
                    }
                }
        )
        $inventory = [ordered]@{
            schema = 'ooki-release-inventory/v1'
            product = 'Ooki Grader'
            version = $Version
            runtime = $Runtime
            selfContained = $true
            signingHook = $signingHookState
            productionSigningClaimed = ($null -ne $signer)
            signerThumbprint = $signerThumbprint
            artifactCount = $artifacts.Count
            artifacts = $artifacts
        }
        $inventoryPath = Join-Path $payload 'release-inventory.json'
        $inventory | ConvertTo-Json -Depth 8 |
            Set-Content -LiteralPath $inventoryPath -Encoding UTF8

        $checksumLines = Get-ChildItem -LiteralPath $payload -File -Recurse |
            Where-Object { $_.Name -ne 'checksums.txt' } |
            Sort-Object {
                [IO.Path]::GetRelativePath($payload, $_.FullName)
            } |
            ForEach-Object {
                $relative = [IO.Path]::GetRelativePath(
                    $payload,
                    $_.FullName).Replace('\', '/')
                $hash = (Get-FileHash -LiteralPath $_.FullName `
                    -Algorithm SHA256).Hash.ToLowerInvariant()
                "$hash  $relative"
            }
        $checksumLines | Set-Content -LiteralPath (
            Join-Path $payload 'checksums.txt') -Encoding ASCII

        [IO.Directory]::Move($payload, $packageRoot)
        [pscustomobject]@{
            state = 'packaged'
            version = $Version
            runtime = $Runtime
            packageRoot = $packageRoot
            hostAndToolSideBySide = $true
            technicianScriptsAtPackageRoot = $true
            checksumManifest = 'checksums.txt'
            inventory = 'release-inventory.json'
            signingHook = $signingHookState
            productionSigningClaimed = ($null -ne $signer)
            signerThumbprint = $signerThumbprint
            externalGates = @(
                'Production Authenticode publisher and trust approval remain external even when a signing hook is supplied.',
                'Release checksums must be published through the controlled distribution channel.',
                'The assembled package still requires clean-install, upgrade, restore, and peer TLS validation on supported Windows.'
            )
        } | ConvertTo-Json -Depth 6
    } finally {
        if ([IO.Directory]::Exists($staging)) {
            [IO.Directory]::Delete($staging, $true)
        }
    }
}
