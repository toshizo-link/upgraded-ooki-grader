[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string] $DataRoot,

    [Parameter(Mandatory)]
    [string] $PackageRoot,

    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$')]
    [string] $Version,

    [string] $BackupRoot,

    [ValidateRange(1, 65535)]
    [int] $HttpsPort = 443,

    [string] $ServiceName = 'OokiGrader.Host',

    [switch] $PassThru,

    [ValidateScript({
        [string]::IsNullOrWhiteSpace($_) -or
        $_ -match '^[A-Fa-f0-9]{40,128}$'
    })]
    [string] $ExpectedSignerThumbprint,

    [switch] $AllowChecksumVerifiedOnSitePackage,

    [switch] $AllowUnsignedDevelopmentBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'OokiGrader.Windows.psm1') -Force

Assert-OokiWindows
if ($AllowChecksumVerifiedOnSitePackage -and
    $AllowUnsignedDevelopmentBuild) {
    throw 'Choose either the physically controlled on-site package mode or the isolated development override, not both.'
}
$allowUnsignedPackage = $AllowChecksumVerifiedOnSitePackage -or
    $AllowUnsignedDevelopmentBuild
$resolvedDataRoot = Assert-OokiDataRoot -DataRoot $DataRoot
$resolvedPackageRoot = Resolve-OokiExactPath -Path $PackageRoot `
    -Purpose 'Package root' -MustExist -PathType Directory
$packageEvidence = Assert-OokiReleasePackage `
    -PackageRoot $resolvedPackageRoot -ExpectedVersion $Version `
    -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
    -AllowUnsignedDevelopmentBuild:$allowUnsignedPackage
$hostExecutable = Join-Path $resolvedPackageRoot 'OokiGrader.Host.exe'
$toolExecutable = Join-Path $resolvedPackageRoot 'OokiGrader.Tool.exe'
$checks = [Collections.Generic.List[object]]::new()

function Add-PreflightCheck {
    param(
        [string] $Name,
        [bool] $Passed,
        [bool] $Blocking,
        [string] $Detail
    )

    $checks.Add([pscustomobject]@{
        name = $Name
        passed = $Passed
        blocking = $Blocking
        classification = if ($Blocking) {
            'requirement'
        } else {
            'recommendation'
        }
        detail = $Detail
    })
}

$os = Get-CimInstance Win32_OperatingSystem
$computer = Get-CimInstance Win32_ComputerSystem
$installedMemoryBytes = @(
    Get-CimInstance Win32_PhysicalMemory -ErrorAction SilentlyContinue |
        Measure-Object -Property Capacity -Sum
).Sum
if ($null -eq $installedMemoryBytes -or $installedMemoryBytes -le 0) {
    $installedMemoryBytes = $computer.TotalPhysicalMemory
}
$isWindows11Pro = [Environment]::OSVersion.Version.Build -ge 22000 -and
    $os.Caption -match 'Windows 11 Pro'
Add-PreflightCheck 'windows-supported' $isWindows11Pro $false `
    'A current, supported Windows 11 Pro build is recommended.'
Add-PreflightCheck 'x64-process' (
    [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq
        [Runtime.InteropServices.Architecture]::X64 -and
    [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq
        [Runtime.InteropServices.Architecture]::X64) $true `
    'An x64 Windows installation and x64 PowerShell process are required.'
Add-PreflightCheck 'memory' ($installedMemoryBytes -ge 16GB) $false `
    'At least 16 GiB installed RAM is recommended; firmware-reserved memory is allowed, and 32 GiB is preferred.'
Add-PreflightCheck 'cpu' ($computer.NumberOfLogicalProcessors -ge 8) $false `
    'Eight logical processors are the pilot minimum.'
Add-PreflightCheck 'time-service' ($null -ne (Get-Service W32Time `
    -ErrorAction SilentlyContinue)) $false `
    'Windows Time should be available and synchronized.'

$driveRoot = [IO.Path]::GetPathRoot($resolvedDataRoot)
$volume = Get-Volume -DriveLetter $driveRoot.Substring(0, 1) `
    -ErrorAction SilentlyContinue
Add-PreflightCheck 'data-volume-ntfs' (
    $null -ne $volume -and $volume.FileSystem -eq 'NTFS') $true `
    'The data root must be on NTFS.'
Add-PreflightCheck 'data-capacity' (
    $null -ne $volume -and $volume.SizeRemaining -ge 165GB) $false `
    'At least 165 GiB free is recommended for the managed quota and reserve.'
Add-PreflightCheck 'data-emergency-reserve' (
    $null -ne $volume -and $volume.SizeRemaining -ge 5GB) $true `
    'At least 5 GiB free is required for the runtime emergency reserve.'

$bitLocker = Get-Command Get-BitLockerVolume -ErrorAction SilentlyContinue
$bitLockerEnabled = $false
if ($null -ne $bitLocker -and $null -ne $volume) {
    $bitLockerVolume = Get-BitLockerVolume -MountPoint $driveRoot `
        -ErrorAction SilentlyContinue
    $bitLockerEnabled = $null -ne $bitLockerVolume -and
        $bitLockerVolume.ProtectionStatus -eq 'On'
}
Add-PreflightCheck 'data-volume-encryption' $bitLockerEnabled $false `
    'BitLocker is recommended for the host data volume.'

$listener = Get-NetTCPConnection -LocalPort $HttpsPort -State Listen `
    -ErrorAction SilentlyContinue
$escapedServiceName = $ServiceName.Replace("'", "''")
$serviceRecord = Get-CimInstance Win32_Service `
    -Filter "Name='$escapedServiceName'" -ErrorAction SilentlyContinue
$foreignListener = @($listener | Where-Object {
    $null -eq $serviceRecord -or
    $_.OwningProcess -ne $serviceRecord.ProcessId
})
Add-PreflightCheck 'https-port-available' (
    $foreignListener.Count -eq 0) $true `
    'The HTTPS port must not be owned by another process.'
$privateProfile = Get-NetConnectionProfile -ErrorAction SilentlyContinue |
    Where-Object NetworkCategory -in @('Private', 'DomainAuthenticated') |
    Select-Object -First 1
Add-PreflightCheck 'private-network-profile' ($null -ne $privateProfile) $false `
    'At least one active network profile should be Private or DomainAuthenticated.'
$privateAddress = Get-NetIPAddress -AddressFamily IPv4 `
    -AddressState Preferred -ErrorAction SilentlyContinue |
    Where-Object {
        $_.IPAddress -match '^10\.' -or
        $_.IPAddress -match '^192\.168\.' -or
        $_.IPAddress -match '^172\.(1[6-9]|2[0-9]|3[01])\.'
    } |
    Select-Object -First 1
Add-PreflightCheck 'private-host-address' ($null -ne $privateAddress) $true `
    'The host requires a private IPv4 address on the school LAN.'

$signature = Assert-OokiAuthenticodeSignature -FilePath $hostExecutable `
    -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
    -AllowUnsignedDevelopmentBuild:$allowUnsignedPackage
$toolSignature = Assert-OokiAuthenticodeSignature -FilePath $toolExecutable `
    -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
    -AllowUnsignedDevelopmentBuild:$allowUnsignedPackage
Add-PreflightCheck 'code-signature' ($signature.Status -eq 'Valid') (
    -not $allowUnsignedPackage) `
    $(if ($AllowChecksumVerifiedOnSitePackage) {
        'The physically controlled on-site package is checksum-verified; Authenticode is recorded as an accepted external gate.'
    } else {
        'Production release binaries must be Authenticode signed.'
    })
Add-PreflightCheck 'release-package-integrity' (
    $packageEvidence.Version -eq $Version -and
    $packageEvidence.Runtime -eq 'win-x64' -and
    $packageEvidence.FileCount -gt 0) $true `
    'The complete self-contained release package must match its checksum inventory.'

if (-not [string]::IsNullOrWhiteSpace($BackupRoot)) {
    $resolvedBackupRoot = Resolve-OokiExactPath -Path $BackupRoot `
        -Purpose 'Backup root'
    Add-PreflightCheck 'backup-separate-root' (
        -not ($resolvedBackupRoot + '\').StartsWith(
            $resolvedDataRoot + '\',
            [StringComparison]::OrdinalIgnoreCase) -and
        -not ($resolvedDataRoot + '\').StartsWith(
            $resolvedBackupRoot + '\',
            [StringComparison]::OrdinalIgnoreCase)) $true `
        'The backup destination must be separate from live data.'
}

$defenderCommand = Get-Command Get-MpComputerStatus `
    -ErrorAction SilentlyContinue
$defender = if ($null -eq $defenderCommand) {
    $null
} else {
    Get-MpComputerStatus -ErrorAction SilentlyContinue
}
Add-PreflightCheck 'defender-health' (
    $null -ne $defender -and $defender.AntivirusEnabled) $false `
    'Microsoft Defender or an approved replacement is recommended.'

$blockingFailures = @($checks | Where-Object {
    $_.blocking -and -not $_.passed
}).Count
$recommendationFailures = @($checks | Where-Object {
    -not $_.blocking -and -not $_.passed
}).Count
$result = [pscustomobject]@{
    state = if ($blockingFailures -eq 0) { 'ready' } else { 'blocked' }
    checkedAt = [DateTimeOffset]::UtcNow
    osCaption = $os.Caption
    blockingFailures = $blockingFailures
    recommendationFailures = $recommendationFailures
    checks = $checks
    externalGates = @(
        'Release signing must be completed by the controlled build pipeline.',
        'Peer CA trust must be validated from an authorized classroom device.',
        'DHCP reservation, DNS, UPS, and school backup ownership require technician sign-off.'
    )
    packageTrustMode = if ($AllowChecksumVerifiedOnSitePackage) {
        'physically-controlled-checksum-verified-on-site-package'
    } elseif ($AllowUnsignedDevelopmentBuild) {
        'isolated-development-override'
    } else {
        'authenticode-signed-production-package'
    }
}
if ($PassThru) {
    return $result
}
$result | ConvertTo-Json -Depth 8

if ($blockingFailures -ne 0) {
    exit 3
}
exit 0
