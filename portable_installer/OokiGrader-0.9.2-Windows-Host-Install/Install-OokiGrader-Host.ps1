#requires -Version 5.1
#requires -RunAsAdministrator

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$version = '0.9.2'
$archiveName = "OokiGrader-$version-win-x64.zip"
$expectedArchiveSha256 = '94d76b2698573016488943c3bd7c8bf80b62f87ab3b41871efe6905c4f35361e'
$mediaRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$archivePath = Join-Path $mediaRoot $archiveName
$archiveChecksumPath = "$archivePath.sha256"
$stagingRoot = 'C:\OokiGrader-Setup'
$packageName = "OokiGrader-$version-win-x64"
$packageRoot = Join-Path $stagingRoot $packageName
$runtimePowerShell = Join-Path $env:SystemRoot `
    'System32\WindowsPowerShell\v1.0\powershell.exe'
$runId = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' +
    [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runStartedAt = [DateTimeOffset]::UtcNow
$diagnosticRoot = Join-Path $stagingRoot 'logs'
$transcriptPath = Join-Path $diagnosticRoot (
    "host-install-$version-$runId.log")
$resultPath = Join-Path $diagnosticRoot (
    "host-install-$version-$runId.result.json")
$transcriptStarted = $false

function Get-Sha256 {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-ChecksumSidecar {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $ExpectedHash,

        [Parameter(Mandatory)]
        [string] $ExpectedFileName
    )

    if (-not [IO.File]::Exists($Path)) {
        throw "Required SHA-256 file is missing: $Path"
    }
    $text = (Get-Content -LiteralPath $Path -Raw).Trim()
    if ($text -notmatch '^(?<hash>[A-Fa-f0-9]{64})  (?<name>[^\r\n]+)$' -or
        -not $Matches.hash.Equals(
            $ExpectedHash,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not $Matches.name.Equals(
            $ExpectedFileName,
            [StringComparison]::Ordinal)) {
        throw "SHA-256 file does not match the pinned media metadata: $Path"
    }
}

function Get-DirectoryFileMap {
    param(
        [Parameter(Mandatory)]
        [string] $Root,

        [string[]] $ExcludedRelativePaths = @()
    )

    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $prefix = $fullRoot + '\'
    $result = @{}
    foreach ($file in Get-ChildItem -LiteralPath $fullRoot `
        -File -Force -Recurse) {
        $relative = $file.FullName.Substring($prefix.Length).Replace('\', '/')
        if ($relative -in $ExcludedRelativePaths) {
            continue
        }
        $result[$relative] = Get-Sha256 -Path $file.FullName
    }
    return $result
}

function Assert-ReleasePackageIntegrity {
    param(
        [Parameter(Mandatory)]
        [string] $Root,

        [Parameter(Mandatory)]
        [string] $ExpectedVersion
    )

    $inventoryPath = Join-Path $Root 'release-inventory.json'
    $checksumPath = Join-Path $Root 'checksums.txt'
    if (-not [IO.File]::Exists($inventoryPath) -or
        -not [IO.File]::Exists($checksumPath)) {
        throw 'The extracted package is missing its inventory or checksums.txt.'
    }
    try {
        $inventory = Get-Content -LiteralPath $inventoryPath -Raw |
            ConvertFrom-Json
    } catch {
        throw 'The extracted release-inventory.json is not valid JSON.'
    }
    if ($inventory.schema -ne 'ooki-release-inventory/v1' -or
        $inventory.product -ne 'Ooki Grader' -or
        $inventory.version -ne $ExpectedVersion -or
        $inventory.runtime -ne 'win-x64' -or
        -not [bool] $inventory.selfContained) {
        throw 'The extracted package product, version, or runtime does not match.'
    }

    $expectedFiles = @{}
    foreach ($line in Get-Content -LiteralPath $checksumPath) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        if ($line -notmatch '^([A-Fa-f0-9]{64})  ([^\r\n]+)$') {
            throw 'The extracted package checksums.txt format is invalid.'
        }
        $expectedHash = $Matches[1].ToLowerInvariant()
        $relative = $Matches[2]
        $segments = @($relative -split '/')
        if ([IO.Path]::IsPathRooted($relative) -or
            $relative.Contains('\') -or
            $segments -contains '..' -or
            $segments -contains '.' -or
            $expectedFiles.ContainsKey($relative)) {
            throw 'The extracted package checksums.txt contains an unsafe path.'
        }
        $target = Join-Path $Root ($relative.Replace('/', '\'))
        if (-not [IO.File]::Exists($target) -or
            -not (Get-Sha256 -Path $target).Equals(
                $expectedHash,
                [StringComparison]::Ordinal)) {
            throw "The extracted package SHA-256 does not match: $relative"
        }
        $expectedFiles[$relative] = $true
    }

    $actualFiles = Get-DirectoryFileMap -Root $Root `
        -ExcludedRelativePaths @('checksums.txt')
    if ($actualFiles.Count -ne $expectedFiles.Count) {
        throw 'The extracted package has missing or extra files.'
    }
    foreach ($relative in $actualFiles.Keys) {
        if (-not $expectedFiles.ContainsKey($relative)) {
            throw "The extracted package contains an unlisted file: $relative"
        }
    }
}

function Test-ExactDirectoryMatch {
    param(
        [Parameter(Mandatory)]
        [string] $Left,

        [Parameter(Mandatory)]
        [string] $Right
    )

    $leftFiles = Get-DirectoryFileMap -Root $Left
    $rightFiles = Get-DirectoryFileMap -Root $Right
    if ($leftFiles.Count -ne $rightFiles.Count) {
        return $false
    }
    foreach ($relative in $leftFiles.Keys) {
        if (-not $rightFiles.ContainsKey($relative) -or
            -not ([string] $leftFiles[$relative]).Equals(
                [string] $rightFiles[$relative],
                [StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }
    return $true
}

function Assert-NoReparsePoints {
    param(
        [Parameter(Mandatory)]
        [string] $Root
    )

    $rootItem = Get-Item -LiteralPath $Root -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The reusable package root cannot be a reparse point: $Root"
    }
    $reparsePoint = Get-ChildItem -LiteralPath $Root -Force -Recurse |
        Where-Object {
            ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        } |
        Select-Object -First 1
    if ($null -ne $reparsePoint) {
        throw "The reusable package contains a reparse point: $($reparsePoint.FullName)"
    }
}

function Write-DiagnosticResult {
    param(
        [Parameter(Mandatory)]
        [string] $State,

        [string] $ErrorMessage
    )

    $result = [ordered]@{
        schema = 'ooki-host-install-run/v1'
        state = $State
        version = $version
        startedAt = $runStartedAt.ToString('O')
        completedAt = [DateTimeOffset]::UtcNow.ToString('O')
        mediaRoot = $mediaRoot
        packageRoot = $packageRoot
        powerShellPath = $runtimePowerShell
        transcriptPath = $transcriptPath
        error = if ([string]::IsNullOrWhiteSpace($ErrorMessage)) {
            $null
        } else {
            $ErrorMessage
        }
    }
    $json = $result | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText(
        $resultPath,
        $json + "`r`n",
        [Text.UTF8Encoding]::new($true))
    return $json
}

trap {
    $failureMessage = $_.Exception.Message
    try {
        Write-DiagnosticResult -State 'failed' `
            -ErrorMessage $failureMessage | Out-Null
    } catch {
        [Console]::Error.WriteLine(
            "Could not write the diagnostic result: $($_.Exception.Message)")
    }
    if ($transcriptStarted) {
        try {
            Stop-Transcript | Out-Null
        } catch {
            [Console]::Error.WriteLine(
                "Could not stop the diagnostic log: $($_.Exception.Message)")
        }
    }
    [Console]::Error.WriteLine("Installation failed: $failureMessage")
    [Console]::Error.WriteLine("Diagnostic result: $resultPath")
    [Console]::Error.WriteLine("Diagnostic log: $transcriptPath")
    exit 1
}

[IO.Directory]::CreateDirectory($diagnosticRoot) | Out-Null
try {
    Start-Transcript -Path $transcriptPath -Force | Out-Null
    $transcriptStarted = $true
} catch {
    Write-Warning "Could not start the diagnostic log: $($_.Exception.Message)"
}

foreach ($requiredFile in @(
    $archivePath,
    $archiveChecksumPath
)) {
    if (-not [IO.File]::Exists($requiredFile)) {
        throw "Required installation media is missing: $requiredFile"
    }
}

Write-Host '1/3 Verifying the installation media SHA-256...'
Assert-ChecksumSidecar -Path $archiveChecksumPath `
    -ExpectedHash $expectedArchiveSha256 -ExpectedFileName $archiveName
if (-not (Get-Sha256 -Path $archivePath).Equals(
    $expectedArchiveSha256,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The Ooki Grader ZIP SHA-256 does not match. Copy the media again.'
}
if (-not [IO.File]::Exists($runtimePowerShell)) {
    throw 'Built-in Windows PowerShell 5.1 was not found. Repair the Windows component.'
}

Write-Host '2/3 Safely extracting the complete Ooki Grader package...'
[IO.Directory]::CreateDirectory($stagingRoot) | Out-Null
$extractRoot = Join-Path $stagingRoot (
    ".staging-$packageName-" + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($extractRoot) | Out-Null
try {
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractRoot
    $stagedPackageRoot = Join-Path $extractRoot $packageName
    if (-not [IO.Directory]::Exists($stagedPackageRoot)) {
        throw 'The ZIP does not contain the expected top-level package folder.'
    }
    $unexpectedItem = Get-ChildItem -LiteralPath $extractRoot -Force |
        Where-Object { $_.FullName -ne $stagedPackageRoot } |
        Select-Object -First 1
    if ($null -ne $unexpectedItem) {
        throw 'The ZIP contains an unexpected top-level file or folder.'
    }
    Assert-ReleasePackageIntegrity -Root $stagedPackageRoot `
        -ExpectedVersion $version
    Assert-NoReparsePoints -Root $stagedPackageRoot

    if ([IO.Directory]::Exists($packageRoot)) {
        Assert-NoReparsePoints -Root $packageRoot
        if (-not (Test-ExactDirectoryMatch `
            -Left $stagedPackageRoot -Right $packageRoot)) {
            throw "$packageRoot exists but does not exactly match the verified ZIP. Existing content will not be overwritten."
        }
        Write-Host 'Reusing an existing package that exactly matches the verified ZIP.'
    } elseif ([IO.File]::Exists($packageRoot)) {
        throw "$packageRoot is not a folder. Existing content will not be overwritten."
    } else {
        [IO.Directory]::Move($stagedPackageRoot, $packageRoot)
    }
} finally {
    if ([IO.Directory]::Exists($extractRoot)) {
        [IO.Directory]::Delete($extractRoot, $true)
    }
}

Write-Host '3/3 Starting the Ooki Grader host setup...'
Write-Host 'The current IP, connected school subnet, data folder, and backup folder will be selected automatically.'
$onSiteArguments = @(
    '-NoLogo',
    '-NoProfile',
    '-ExecutionPolicy',
    'Bypass',
    '-File',
    (Join-Path $packageRoot 'Install-OokiGraderOnSite.ps1'),
    '-PackageRoot',
    $packageRoot
)
& $runtimePowerShell @onSiteArguments
if ($LASTEXITCODE -ne 0) {
    throw "Ooki Grader setup stopped with exit code $LASTEXITCODE."
}

$successJson = Write-DiagnosticResult -State 'installed-and-verified'
if ($transcriptStarted) {
    Stop-Transcript | Out-Null
    $transcriptStarted = $false
}
Write-Host 'Ooki Grader host setup completed successfully.'
Write-Host "Diagnostic result: $resultPath"
Write-Host "Diagnostic log: $transcriptPath"
$successJson
