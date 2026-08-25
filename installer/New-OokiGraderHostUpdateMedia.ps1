#requires -Version 7.4

<#
.SYNOPSIS
Builds self-checking portable update media for an existing Ooki Grader host.

.EXAMPLE
pwsh -NoLogo -NoProfile -File .\installer\New-OokiGraderHostUpdateMedia.ps1 `
  -PackageRoot 'C:\Releases\OokiGrader-0.9.3-win-x64' `
  -Version '0.9.3' `
  -OutputRoot 'C:\Releases\host-update-media' `
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

    [string] $ExpectedSignerThumbprint,

    [switch] $AllowChecksumVerifiedUnsignedOnSitePackage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'OokiGrader.Windows.psm1') -Force

function Write-Utf8Bom {
    param([string] $Path, [string] $Text)
    $normalized = $Text.Replace("`r`n", "`n").Replace("`r", "`n")
    [IO.File]::WriteAllText(
        $Path,
        $normalized.Replace("`n", "`r`n"),
        [Text.UTF8Encoding]::new($true))
}

function Write-Utf8NoBom {
    param([string] $Path, [string] $Text)
    $normalized = $Text.Replace("`r`n", "`n").Replace("`r", "`n")
    [IO.File]::WriteAllText(
        $Path,
        $normalized.Replace("`n", "`r`n"),
        [Text.UTF8Encoding]::new($false))
}

$output = Resolve-OokiExactPath -Path $OutputRoot `
    -Purpose 'Host update media output root'
$targetName = "OokiGrader-$Version-Windows-Host-Update"
$target = Join-Path $output $targetName
if ([IO.Directory]::Exists($target) -or [IO.File]::Exists($target)) {
    throw 'Host update media outputs are immutable; the requested version already exists.'
}

if ($PSCmdlet.ShouldProcess(
    $target,
    'Build, verify, and atomically publish portable host update media')) {
    [IO.Directory]::CreateDirectory($output) | Out-Null
    $temporaryRoot = Join-Path $output (
        '.update-media-source-' + [Guid]::NewGuid().ToString('N'))
    $staging = Join-Path $output (
        ".staging-$targetName-" + [Guid]::NewGuid().ToString('N'))
    try {
        $builderArguments = @{
            PackageRoot = $PackageRoot
            Version = $Version
            OutputRoot = $temporaryRoot
            ExpectedSignerThumbprint = $ExpectedSignerThumbprint
            AllowChecksumVerifiedUnsignedOnSitePackage = `
                $AllowChecksumVerifiedUnsignedOnSitePackage
            Confirm = $false
        }
        & (Join-Path $PSScriptRoot `
            'New-OokiGraderHostInstallMedia.ps1') @builderArguments |
            Out-Null

        $installMedia = Join-Path $temporaryRoot `
            "OokiGrader-$Version-Windows-Host-Install"
        [IO.Directory]::Move($installMedia, $staging)
        foreach ($obsolete in @(
            '00-README-ja.txt',
            '01-Install-OokiGrader-Host.cmd',
            'checksums.txt'
        )) {
            [IO.File]::Delete((Join-Path $staging $obsolete))
        }

        $installBootstrapPath = Join-Path $staging `
            'Install-OokiGrader-Host.ps1'
        $bootstrap = [IO.File]::ReadAllText($installBootstrapPath)
        $phaseMarker = "Write-Host '3/3 Starting the Ooki Grader host setup...'"
        $phaseIndex = $bootstrap.IndexOf(
            $phaseMarker,
            [StringComparison]::Ordinal)
        if ($phaseIndex -lt 0) {
            throw 'The portable installer bootstrap no longer has the expected phase boundary.'
        }
        $bootstrap = $bootstrap.Substring(0, $phaseIndex)
        $updatePhase = [IO.File]::ReadAllText((Join-Path (
            Join-Path $PSScriptRoot 'HostUpdateMedia') `
            'Update-Phase.ps1.template'))
        $bootstrap = $bootstrap + $updatePhase.Replace(
            '@@OOKI_VERSION@@',
            $Version)
        $bootstrap = $bootstrap.Replace(
            'host-install-',
            'host-update-').Replace(
            "schema = 'ooki-host-install-run/v1'",
            "schema = 'ooki-host-update-run/v1'").Replace(
            'Installation failed:',
            'Update failed:').Replace(
            "Write-DiagnosticResult -State 'installed-and-verified'",
            "Write-DiagnosticResult -State 'updated-and-verified'")
        [IO.File]::Delete($installBootstrapPath)
        Write-Utf8Bom -Path (Join-Path $staging `
            'Update-OokiGrader-Host.ps1') -Text $bootstrap

        $launcher = [IO.File]::ReadAllText((Join-Path (
            Join-Path $PSScriptRoot 'HostUpdateMedia') `
            '01-Update-OokiGrader-Host.cmd.template'))
        Write-Utf8NoBom -Path (Join-Path $staging `
            '01-Update-OokiGrader-Host.cmd') -Text $launcher

        $readme = [IO.File]::ReadAllText((Join-Path (
            Join-Path $PSScriptRoot 'HostUpdateMedia') `
            '00-README-ja.txt.template')).Replace(
            '@@OOKI_VERSION@@',
            $Version)
        Write-Utf8Bom -Path (Join-Path $staging `
            '00-README-ja.txt') -Text $readme

        $inventoryPath = Join-Path $staging 'media-inventory.json'
        $inventory = Get-Content -LiteralPath $inventoryPath -Raw |
            ConvertFrom-Json
        $inventory.schema = 'ooki-host-update-media/v1'
        $inventory | Add-Member -NotePropertyName updateEntryPoint `
            -NotePropertyValue 'Update-OokiGrader-Host.ps1'
        $artifactFiles = @(Get-ChildItem -LiteralPath $staging `
            -File -Force -Recurse | Where-Object {
                $_.Name -notin @('media-inventory.json', 'checksums.txt')
            } | Sort-Object FullName)
        $artifactPrefix = $staging.TrimEnd('\') + '\'
        $inventory.artifacts = @($artifactFiles | ForEach-Object {
            [ordered]@{
                path = $_.FullName.Substring($artifactPrefix.Length).Replace(
                    '\',
                    '/')
                bytes = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName `
                    -Algorithm SHA256).Hash.ToLowerInvariant()
                kind = if ($_.Extension -eq '.zip') {
                    'release-package-archive'
                } elseif ($_.Extension -in @('.ps1', '.cmd')) {
                    'bootstrap'
                } else {
                    'operator-documentation'
                }
            }
        })
        $inventory.artifactCount = $inventory.artifacts.Count
        $inventoryJson = $inventory | ConvertTo-Json -Depth 10
        Write-Utf8NoBom -Path $inventoryPath -Text ($inventoryJson + "`n")

        $files = @(Get-ChildItem -LiteralPath $staging -File -Force -Recurse |
            Sort-Object FullName)
        $prefix = $staging.TrimEnd('\') + '\'
        $checksumLines = foreach ($file in $files) {
            $relative = $file.FullName.Substring($prefix.Length).Replace(
                '\',
                '/')
            $hash = (Get-FileHash -LiteralPath $file.FullName `
                -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $relative"
        }
        [IO.File]::WriteAllLines(
            (Join-Path $staging 'checksums.txt'),
            $checksumLines,
            [Text.ASCIIEncoding]::new())

        $required = @(
            '00-README-ja.txt',
            '01-Update-OokiGrader-Host.cmd',
            'Update-OokiGrader-Host.ps1',
            "OokiGrader-$Version-win-x64.zip",
            "OokiGrader-$Version-win-x64.zip.sha256",
            'media-inventory.json',
            'checksums.txt'
        )
        foreach ($name in $required) {
            if (-not [IO.File]::Exists((Join-Path $staging $name))) {
                throw "The portable update media is missing $name."
            }
        }
        [IO.Directory]::Move($staging, $target)
        [pscustomobject]@{
            state = 'update-media-packaged-and-verified'
            version = $Version
            mediaRoot = $target
            entryPoint = '01-Update-OokiGrader-Host.cmd'
            atomicPublish = $true
        } | ConvertTo-Json -Depth 5
    } finally {
        if ([IO.Directory]::Exists($temporaryRoot)) {
            [IO.Directory]::Delete($temporaryRoot, $true)
        }
        if ([IO.Directory]::Exists($staging)) {
            [IO.Directory]::Delete($staging, $true)
        }
    }
}
