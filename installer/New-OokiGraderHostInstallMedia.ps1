#requires -Version 7.4

<#
.SYNOPSIS
Builds byte-reproducible, self-checking Windows host installation media.

.EXAMPLE
pwsh -NoLogo -NoProfile -File .\installer\New-OokiGraderHostInstallMedia.ps1 `
  -PackageRoot 'C:\OokiGrader-Releases\OokiGrader-0.9.2-win-x64' `
  -Version '0.9.2' `
  -OutputRoot 'C:\OokiGrader-Releases\host-install-media' `
  -AllowChecksumVerifiedUnsignedOnSitePackage
#>

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory)]
    [string] $PackageRoot,

    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$')]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $OutputRoot,

    [ValidateScript({
        [string]::IsNullOrWhiteSpace($_) -or
        $_ -match '^[A-Fa-f0-9]{40,128}$'
    })]
    [string] $ExpectedSignerThumbprint,

    [switch] $AllowChecksumVerifiedUnsignedOnSitePackage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'OokiGrader.Windows.psm1') -Force

$packageName = "OokiGrader-$Version-win-x64"
$mediaName = "OokiGrader-$Version-Windows-Host-Install"
$fixedArchiveTimestamp = [DateTimeOffset]::new(
    1980,
    1,
    1,
    0,
    0,
    0,
    [TimeSpan]::Zero)

function Write-MediaTextFile {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $Content,

        [ValidateSet('Ascii', 'Utf8NoBom', 'Utf8Bom')]
        [string] $Encoding = 'Utf8NoBom'
    )

    $normalized = $Content.Replace("`r`n", "`n").Replace("`r", "`n")
    $normalized = $normalized.Replace("`n", "`r`n")
    $encoder = switch ($Encoding) {
        'Ascii' { [Text.ASCIIEncoding]::new() }
        'Utf8Bom' { [Text.UTF8Encoding]::new($true) }
        default { [Text.UTF8Encoding]::new($false) }
    }
    [IO.File]::WriteAllText($Path, $normalized, $encoder)
}

function Read-MediaTemplate {
    param(
        [Parameter(Mandatory)]
        [string] $Name
    )

    $path = Join-Path (Join-Path $PSScriptRoot 'HostInstallMedia') $Name
    if (-not [IO.File]::Exists($path)) {
        throw "The host-install-media template is missing: $Name"
    }
    return [IO.File]::ReadAllText($path)
}

function New-DeterministicPackageArchive {
    param(
        [Parameter(Mandatory)]
        [string] $Source,

        [Parameter(Mandatory)]
        [string] $Destination,

        [Parameter(Mandatory)]
        [string] $EntryRoot,

        [Parameter(Mandatory)]
        [DateTimeOffset] $EntryTimestamp
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $relativePaths = [string[]] @(
        Get-ChildItem -LiteralPath $Source -File -Force -Recurse |
            ForEach-Object {
                [IO.Path]::GetRelativePath($Source, $_.FullName).Replace(
                    '\',
                    '/')
            }
    )
    [Array]::Sort($relativePaths, [StringComparer]::Ordinal)
    if ($relativePaths.Count -eq 0) {
        throw 'The release package contains no files to archive.'
    }

    $stream = [IO.FileStream]::new(
        $Destination,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None)
    $archive = $null
    try {
        $archive = [IO.Compression.ZipArchive]::new(
            $stream,
            [IO.Compression.ZipArchiveMode]::Create,
            $true,
            [Text.Encoding]::UTF8)
        foreach ($relative in $relativePaths) {
            $sourcePath = Join-Path $Source ($relative.Replace(
                '/',
                [IO.Path]::DirectorySeparatorChar))
            $entry = $archive.CreateEntry(
                "$EntryRoot/$relative",
                [IO.Compression.CompressionLevel]::NoCompression)
            $entry.LastWriteTime = $EntryTimestamp
            $input = [IO.File]::OpenRead($sourcePath)
            $output = $null
            try {
                $output = $entry.Open()
                $input.CopyTo($output)
            } finally {
                if ($null -ne $output) {
                    $output.Dispose()
                }
                $input.Dispose()
            }
        }
    } finally {
        if ($null -ne $archive) {
            $archive.Dispose()
        }
        $stream.Dispose()
    }
}

function Get-MediaArtifacts {
    param(
        [Parameter(Mandatory)]
        [string] $Root,

        [string[]] $ExcludedNames = @()
    )

    $files = @(
        Get-ChildItem -LiteralPath $Root -File -Force -Recurse |
            Where-Object { $_.Name -notin $ExcludedNames }
    )
    $relativePaths = [string[]] @($files | ForEach-Object {
        [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
    })
    [Array]::Sort($relativePaths, [StringComparer]::Ordinal)
    return @($relativePaths | ForEach-Object {
        $relative = $_
        $path = Join-Path $Root ($relative.Replace(
            '/',
            [IO.Path]::DirectorySeparatorChar))
        $file = [IO.FileInfo]::new($path)
        [ordered]@{
            path = $relative
            bytes = $file.Length
            sha256 = (Get-FileHash -LiteralPath $path `
                -Algorithm SHA256).Hash.ToLowerInvariant()
            kind = if ($relative.EndsWith('.zip')) {
                'release-package-archive'
            } elseif ($relative.EndsWith('.msi')) {
                'signed-prerequisite'
            } elseif ($relative.EndsWith('.sha256')) {
                'checksum-sidecar'
            } elseif ($relative.EndsWith('.ps1') -or
                $relative.EndsWith('.cmd')) {
                'bootstrap'
            } else {
                'operator-documentation'
            }
        }
    })
}

function Assert-MediaChecksums {
    param(
        [Parameter(Mandatory)]
        [string] $Root,

        [Parameter(Mandatory)]
        [string] $ChecksumPath
    )

    $expectedFiles = @{}
    foreach ($line in [IO.File]::ReadAllLines($ChecksumPath)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        if ($line -notmatch '^([A-Fa-f0-9]{64})  ([^\r\n]+)$') {
            throw 'The aggregate media checksum file has an invalid line.'
        }
        $expectedHash = $Matches[1].ToLowerInvariant()
        $relative = $Matches[2]
        $segments = @($relative -split '/')
        if ([IO.Path]::IsPathRooted($relative) -or
            $relative.Contains('\') -or
            $segments -contains '..' -or
            $segments -contains '.' -or
            $expectedFiles.ContainsKey($relative)) {
            throw 'The aggregate media checksum file contains an unsafe or duplicate path.'
        }
        $path = Join-Path $Root ($relative.Replace(
            '/',
            [IO.Path]::DirectorySeparatorChar))
        if (-not [IO.File]::Exists($path)) {
            throw "The aggregate media checksum references a missing file: $relative"
        }
        $actualHash = (Get-FileHash -LiteralPath $path `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        if (-not $actualHash.Equals(
            $expectedHash,
            [StringComparison]::Ordinal)) {
            throw "The aggregate media checksum failed for $relative."
        }
        $expectedFiles[$relative] = $true
    }

    $rootPrefix = $Root.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    $actualFiles = @(Get-ChildItem -LiteralPath $Root -File -Force -Recurse |
        Where-Object { $_.FullName -ne $ChecksumPath } |
        ForEach-Object {
            $_.FullName.Substring($rootPrefix.Length).Replace(
                [IO.Path]::DirectorySeparatorChar,
                '/')
        })
    if ($actualFiles.Count -ne $expectedFiles.Count) {
        throw 'The aggregate media checksums do not cover every media file.'
    }
    foreach ($relative in $actualFiles) {
        if (-not $expectedFiles.ContainsKey($relative)) {
            throw "The host install media contains an unlisted file: $relative"
        }
    }
}

Assert-OokiWindows
$packageEvidence = Assert-OokiReleasePackage `
    -PackageRoot $PackageRoot `
    -ExpectedVersion $Version `
    -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
    -AllowUnsignedDevelopmentBuild:$AllowChecksumVerifiedUnsignedOnSitePackage
if (-not $AllowChecksumVerifiedUnsignedOnSitePackage -and
    -not $packageEvidence.ProductionSigningClaimed) {
    throw 'A signed media build requires a production-signed release package.'
}
$package = $packageEvidence.Root
$releaseInventory = Get-Content -LiteralPath (
    Join-Path $package 'release-inventory.json') -Raw | ConvertFrom-Json
if ([string] $releaseInventory.hostInstallEntryPoint -ne
        'Install-OokiGraderOnSite.ps1' -or
    [string] $releaseInventory.minimumTechnicianPowerShell -ne '5.1' -or
    [string] $releaseInventory.technicianScriptEncoding -ne 'utf-8-bom') {
    throw 'The release package does not declare a Windows PowerShell 5.1-compatible host installer.'
}
$requiredHostInstallerFiles = @(
    'Install-OokiGraderOnSite.ps1',
    'Install-OokiGrader.ps1',
    'Test-OokiGraderPreflight.ps1',
    'Test-OokiGraderHealth.ps1',
    'New-OokiGraderCertificate.ps1',
    'New-OokiGraderPeerTrustPackage.ps1',
    'Install-OokiGraderPeerTrust.ps1',
    'OokiGrader.Windows.psm1'
)
foreach ($requiredHostInstallerFile in $requiredHostInstallerFiles) {
    if (-not [IO.File]::Exists((
            Join-Path $package $requiredHostInstallerFile))) {
        throw "The release package is missing the host installer file $requiredHostInstallerFile."
    }
}
$packageChecksumPath = Join-Path $package 'checksums.txt'
$allPackagePayloadFiles = @(Get-ChildItem -LiteralPath $package `
    -File -Force -Recurse | Where-Object {
        $_.FullName -ne $packageChecksumPath
    })
if ($allPackagePayloadFiles.Count -ne $packageEvidence.FileCount) {
    throw 'The release package contains a hidden or otherwise unlisted file.'
}

$reparsePoint = Get-ChildItem -LiteralPath $package -Force -Recurse |
    Where-Object {
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    } |
    Select-Object -First 1
if ($null -ne $reparsePoint) {
    throw 'The release package may not contain reparse points or symbolic links.'
}

$output = Resolve-OokiExactPath -Path $OutputRoot `
    -Purpose 'Host install media output root'
$resolvedRoots = Assert-OokiDisjointPaths -Paths @{
    'Release package root' = $package
    'Host install media output root' = $output
}
$package = [string] $resolvedRoots['Release package root']
$output = [string] $resolvedRoots['Host install media output root']
$target = Join-Path $output $mediaName
if ([IO.Directory]::Exists($target) -or [IO.File]::Exists($target)) {
    throw 'Host install media outputs are immutable; the requested version already exists.'
}

if ($PSCmdlet.ShouldProcess(
    $target,
    'Build, verify, and atomically publish reproducible host install media')) {
    [IO.Directory]::CreateDirectory($output) | Out-Null
    $staging = Join-Path $output (
        ".staging-$mediaName-" + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($staging) | Out-Null
    try {
        $archiveName = "$packageName.zip"
        $archivePath = Join-Path $staging $archiveName
        New-DeterministicPackageArchive -Source $package `
            -Destination $archivePath -EntryRoot $packageName `
            -EntryTimestamp $fixedArchiveTimestamp
        $archiveHash = (Get-FileHash -LiteralPath $archivePath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-MediaTextFile -Path "$archivePath.sha256" `
            -Content "$archiveHash  $archiveName`n" -Encoding Ascii

        $bootstrap = (Read-MediaTemplate `
            -Name 'Install-OokiGrader-Host.ps1.template').Replace(
                '@@OOKI_VERSION@@',
                $Version).Replace(
                '@@PACKAGE_ZIP_SHA256@@',
                $archiveHash)
        if ($bootstrap.Contains('@@')) {
            throw 'The rendered PowerShell bootstrap contains an unresolved template token.'
        }
        Write-MediaTextFile -Path (Join-Path $staging `
            'Install-OokiGrader-Host.ps1') -Content $bootstrap `
            -Encoding Utf8Bom

        $launcher = Read-MediaTemplate `
            -Name '01-Install-OokiGrader-Host.cmd.template'
        Write-MediaTextFile -Path (Join-Path $staging `
            '01-Install-OokiGrader-Host.cmd') -Content $launcher `
            -Encoding Utf8NoBom

        $readme = (Read-MediaTemplate `
            -Name '00-README-ja.txt.template').Replace(
                '@@OOKI_VERSION@@',
                $Version)
        if ($readme.Contains('@@')) {
            throw 'The rendered media README contains an unresolved template token.'
        }
        Write-MediaTextFile -Path (Join-Path $staging `
            '00-README-ja.txt') -Content $readme -Encoding Utf8Bom

        $verificationRoot = Join-Path $staging '.archive-verification'
        [IO.Directory]::CreateDirectory($verificationRoot) | Out-Null
        try {
            [IO.Compression.ZipFile]::ExtractToDirectory(
                $archivePath,
                $verificationRoot)
            $extractedPackage = Join-Path $verificationRoot $packageName
            Assert-OokiReleasePackage -PackageRoot $extractedPackage `
                -ExpectedVersion $Version `
                -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
                -AllowUnsignedDevelopmentBuild:$AllowChecksumVerifiedUnsignedOnSitePackage |
                Out-Null
        } finally {
            if ([IO.Directory]::Exists($verificationRoot)) {
                [IO.Directory]::Delete($verificationRoot, $true)
            }
        }

        $artifacts = Get-MediaArtifacts -Root $staging
        $inventory = [ordered]@{
            schema = 'ooki-host-install-media/v1'
            product = 'Ooki Grader'
            version = $Version
            runtime = 'win-x64'
            packageArchive = [ordered]@{
                path = $archiveName
                sha256 = $archiveHash
                compression = 'store'
                deterministicEntryTimestamp =
                    $fixedArchiveTimestamp.ToString('O')
                topLevelDirectory = $packageName
            }
            sourcePackage = [ordered]@{
                fileCount = $packageEvidence.FileCount
                productionSigningClaimed =
                    $packageEvidence.ProductionSigningClaimed
                signerThumbprint = $packageEvidence.SignerThumbprint
                hostInstallEntryPoint =
                    [string] $releaseInventory.hostInstallEntryPoint
                minimumTechnicianPowerShell =
                    [string] $releaseInventory.minimumTechnicianPowerShell
            }
            minimumBootstrapPowerShell = '5.1'
            requiredApplicationPowerShell = '5.1 (included with Windows)'
            bundledPrerequisites = @()
            artifactCount = $artifacts.Count
            artifacts = $artifacts
        }
        $inventoryJson = $inventory | ConvertTo-Json -Depth 8
        Write-MediaTextFile -Path (Join-Path $staging `
            'media-inventory.json') -Content "$inventoryJson`n" `
            -Encoding Utf8NoBom

        $checksumArtifacts = Get-MediaArtifacts -Root $staging `
            -ExcludedNames @('checksums.txt')
        $checksumLines = @($checksumArtifacts | ForEach-Object {
            "$($_.sha256)  $($_.path)"
        })
        Write-MediaTextFile -Path (Join-Path $staging 'checksums.txt') `
            -Content (($checksumLines -join "`n") + "`n") -Encoding Ascii

        $aggregateChecksumPath = Join-Path $staging 'checksums.txt'
        Assert-MediaChecksums -Root $staging `
            -ChecksumPath $aggregateChecksumPath
        if (-not ((Get-FileHash -LiteralPath $archivePath `
            -Algorithm SHA256).Hash.ToLowerInvariant()).Equals(
                $archiveHash,
                [StringComparison]::Ordinal)) {
            throw 'The final release ZIP does not match its embedded SHA-256.'
        }
        [IO.Directory]::Move($staging, $target)
        [pscustomobject]@{
            state = 'packaged-and-verified'
            version = $Version
            mediaRoot = $target
            packageArchive = $archiveName
            packageSha256 = $archiveHash
            aggregateInventory = 'media-inventory.json'
            aggregateChecksums = 'checksums.txt'
            atomicPublish = $true
        } | ConvertTo-Json -Depth 6
    } finally {
        if ([IO.Directory]::Exists($staging)) {
            [IO.Directory]::Delete($staging, $true)
        }
    }
}
