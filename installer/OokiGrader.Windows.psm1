Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-OokiWindows {
    [CmdletBinding()]
    param()

    if ([Environment]::OSVersion.Platform -ne
        [PlatformID]::Win32NT) {
        throw 'This operation is supported only on Windows.'
    }
}

function Assert-OokiAdministrator {
    [CmdletBinding()]
    param()

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this technician operation from an elevated PowerShell session.'
    }
}

function Resolve-OokiExactPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $Purpose,

        [switch] $MustExist,

        [ValidateSet('Any', 'File', 'Directory')]
        [string] $PathType = 'Any'
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or
        [Management.Automation.WildcardPattern]::ContainsWildcardCharacters($Path) -or
        -not [IO.Path]::IsPathRooted($Path)) {
        throw "$Purpose must be an absolute path without wildcards."
    }

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $root = [IO.Path]::GetPathRoot($fullPath).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    if ($fullPath.Equals($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Purpose may not be a filesystem root."
    }

    $inspectionPath = $fullPath
    while (-not [string]::IsNullOrWhiteSpace($inspectionPath)) {
        if ([IO.Directory]::Exists($inspectionPath)) {
            $attributes = [IO.File]::GetAttributes($inspectionPath)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Purpose may not traverse a reparse point or symbolic link."
            }
        }
        $parentPath = Split-Path -Parent $inspectionPath
        if ([string]::IsNullOrWhiteSpace($parentPath) -or
            $parentPath -eq $inspectionPath) {
            break
        }
        $inspectionPath = $parentPath
    }

    if ($MustExist) {
        if ($PathType -eq 'File' -and -not [IO.File]::Exists($fullPath)) {
            throw "$Purpose does not identify an existing file."
        }
        if ($PathType -eq 'Directory' -and
            -not [IO.Directory]::Exists($fullPath)) {
            throw "$Purpose does not identify an existing directory."
        }
        if ($PathType -eq 'Any' -and
            -not [IO.File]::Exists($fullPath) -and
            -not [IO.Directory]::Exists($fullPath)) {
            throw "$Purpose does not identify an existing path."
        }
    }

    return $fullPath
}

function Assert-OokiDisjointPaths {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [hashtable] $Paths
    )

    $resolved = [ordered]@{}
    foreach ($entry in $Paths.GetEnumerator()) {
        if ([string]::IsNullOrWhiteSpace([string] $entry.Value)) {
            continue
        }
        $resolved[$entry.Key] = Resolve-OokiExactPath `
            -Path ([string] $entry.Value) -Purpose ([string] $entry.Key)
    }

    $names = @($resolved.Keys)
    for ($leftIndex = 0; $leftIndex -lt $names.Count; $leftIndex++) {
        for ($rightIndex = $leftIndex + 1;
            $rightIndex -lt $names.Count;
            $rightIndex++) {
            $leftName = $names[$leftIndex]
            $rightName = $names[$rightIndex]
            $left = ([string] $resolved[$leftName]).TrimEnd('\')
            $right = ([string] $resolved[$rightName]).TrimEnd('\')
            if ($left.Equals($right, [StringComparison]::OrdinalIgnoreCase) -or
                ($left + '\').StartsWith(
                    $right + '\',
                    [StringComparison]::OrdinalIgnoreCase) -or
                ($right + '\').StartsWith(
                    $left + '\',
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "$leftName and $rightName must be separate, non-overlapping roots."
            }
        }
    }

    return $resolved
}

function Assert-OokiInstallRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $InstallRoot
    )

    $resolved = Resolve-OokiExactPath -Path $InstallRoot `
        -Purpose 'Install root'
    if ($resolved.StartsWith('\\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The install root must be on a local filesystem.'
    }
    $programFiles = Resolve-OokiExactPath -Path $env:ProgramFiles `
        -Purpose 'Program Files' -MustExist -PathType Directory
    if (-not ($resolved + '\').StartsWith(
        $programFiles.TrimEnd('\') + '\',
        [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The production install root must be under Program Files.'
    }

    return $resolved
}

function Assert-OokiDataRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $DataRoot
    )

    $resolved = Resolve-OokiExactPath -Path $DataRoot -Purpose 'Data root'
    if ($resolved.StartsWith(
        '\\',
        [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The live data root must be on a local NTFS volume, not a UNC path.'
    }
    $rejectedParents = @(
        $env:SystemRoot,
        $env:ProgramFiles,
        ${env:ProgramFiles(x86)},
        $env:USERPROFILE,
        $env:TEMP,
        $env:TMP,
        $env:OneDrive,
        $env:OneDriveCommercial,
        $env:OneDriveConsumer
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $rejectedParents) {
        $parent = [IO.Path]::GetFullPath($candidate).TrimEnd('\') + '\'
        if (($resolved + '\').StartsWith(
            $parent,
            [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The data root may not be under Windows, a user profile, temporary storage, or synchronized cloud storage.'
        }
    }

    return $resolved
}

function Assert-OokiAuthenticodeSignature {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,

        [switch] $AllowUnsignedDevelopmentBuild,

        [ValidateScript({
            [string]::IsNullOrWhiteSpace($_) -or
            $_ -match '^[A-Fa-f0-9]{40,128}$'
        })]
        [string] $ExpectedSignerThumbprint
    )

    $resolved = Resolve-OokiExactPath -Path $FilePath `
        -Purpose 'Release executable' -MustExist -PathType File
    $signature = Get-AuthenticodeSignature -LiteralPath $resolved
    if ($signature.Status -ne 'Valid' -and
        -not $AllowUnsignedDevelopmentBuild) {
        throw 'The release executable is not signed by a trusted publisher. Production installation is blocked.'
    }
    if (-not $AllowUnsignedDevelopmentBuild) {
        if ([string]::IsNullOrWhiteSpace($ExpectedSignerThumbprint)) {
            throw 'Production signature validation requires the approved signer thumbprint.'
        }
        $actualThumbprint = if ($null -eq $signature.SignerCertificate) {
            ''
        } else {
            $signature.SignerCertificate.Thumbprint.Replace(' ', '')
        }
        if (-not $actualThumbprint.Equals(
            $ExpectedSignerThumbprint.Replace(' ', ''),
            [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The release file was not signed by the approved publisher certificate.'
        }
    }

    [pscustomobject]@{
        Status = [string] $signature.Status
        SignerSubject = if ($null -eq $signature.SignerCertificate) {
            $null
        } else {
            $signature.SignerCertificate.Subject
        }
        ExternalGate = if ($signature.Status -eq 'Valid') {
            'passed-on-this-host'
        } else {
            'development-override-only'
        }
    }
}

function Assert-OokiReleasePackage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $PackageRoot,

        [Parameter(Mandatory)]
        [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$')]
        [string] $ExpectedVersion,

        [ValidateScript({
            [string]::IsNullOrWhiteSpace($_) -or
            $_ -match '^[A-Fa-f0-9]{40,128}$'
        })]
        [string] $ExpectedSignerThumbprint,

        [switch] $AllowUnsignedDevelopmentBuild
    )

    $root = Resolve-OokiExactPath -Path $PackageRoot `
        -Purpose 'Release package root' -MustExist -PathType Directory
    $checksumPath = Join-Path $root 'checksums.txt'
    $inventoryPath = Join-Path $root 'release-inventory.json'
    if (-not [IO.File]::Exists($checksumPath) -or
        -not [IO.File]::Exists($inventoryPath)) {
        throw 'The release package is missing its inventory or checksum manifest.'
    }

    try {
        $inventory = Get-Content -LiteralPath $inventoryPath -Raw |
            ConvertFrom-Json
    } catch {
        throw 'The release inventory is not valid JSON.'
    }
    if ($inventory.schema -ne 'ooki-release-inventory/v1' -or
        $inventory.product -ne 'Ooki Grader' -or
        $inventory.version -ne $ExpectedVersion -or
        $inventory.runtime -ne 'win-x64' -or
        -not [bool] $inventory.selfContained) {
        throw 'The release inventory does not match the requested Ooki Grader version and runtime.'
    }

    $expectedFiles = @{}
    foreach ($line in Get-Content -LiteralPath $checksumPath) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        if ($line -notmatch '^([A-Fa-f0-9]{64})  ([^\r\n]+)$') {
            throw 'The release checksum manifest has an invalid line.'
        }
        $expectedHash = $Matches[1].ToLowerInvariant()
        $relative = $Matches[2]
        $segments = @($relative -split '/')
        if ([IO.Path]::IsPathRooted($relative) -or
            $relative.Contains('\') -or
            $segments -contains '..' -or
            $segments -contains '.' -or
            $expectedFiles.ContainsKey($relative)) {
            throw 'The release checksum manifest contains an unsafe or duplicate path.'
        }
        $target = Join-Path $root ($relative.Replace(
            '/',
            [IO.Path]::DirectorySeparatorChar))
        if (-not [IO.File]::Exists($target)) {
            throw "The release package is missing $relative."
        }
        $actualHash = (Get-FileHash -LiteralPath $target `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        if (-not $actualHash.Equals(
            $expectedHash,
            [StringComparison]::Ordinal)) {
            throw "The release checksum failed for $relative."
        }
        $expectedFiles[$relative] = $true
    }

    $rootPrefix = $root.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    $actualRelativePaths = @(Get-ChildItem -LiteralPath $root -File -Recurse |
        Where-Object { $_.FullName -ne $checksumPath } |
        ForEach-Object {
            $_.FullName.Substring($rootPrefix.Length).Replace(
                [IO.Path]::DirectorySeparatorChar,
                '/')
        })
    foreach ($relative in $actualRelativePaths) {
        if (-not $expectedFiles.ContainsKey($relative)) {
            throw "The release package contains an unlisted file: $relative"
        }
    }
    if ($actualRelativePaths.Count -ne $expectedFiles.Count) {
        throw 'The release checksum manifest does not cover the complete package.'
    }

    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        $signableFiles = @(Get-ChildItem -LiteralPath $root -File -Recurse |
            Where-Object {
                $_.Extension -in @('.exe', '.dll', '.ps1', '.psm1')
            })
        foreach ($file in $signableFiles) {
            Assert-OokiAuthenticodeSignature -FilePath $file.FullName `
                -ExpectedSignerThumbprint $ExpectedSignerThumbprint `
                -AllowUnsignedDevelopmentBuild:$AllowUnsignedDevelopmentBuild |
                Out-Null
        }
    } elseif (-not $AllowUnsignedDevelopmentBuild) {
        throw 'Production Authenticode validation requires Windows.'
    }

    [pscustomobject]@{
        Root = $root
        Version = [string] $inventory.version
        Runtime = [string] $inventory.runtime
        SelfContained = [bool] $inventory.selfContained
        FileCount = $expectedFiles.Count
        ProductionSigningClaimed = [bool] $inventory.productionSigningClaimed
        SignerThumbprint = $inventory.signerThumbprint
    }
}

function Invoke-OokiNative {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,

        [Parameter(Mandatory)]
        [string[]] $ArgumentList
    )

    $nativeOutput = & $FilePath @ArgumentList 2>&1
    $exitCode = $LASTEXITCODE
    if ($null -ne $nativeOutput) {
        $nativeOutput | Out-Host
    }
    if ($exitCode -ne 0) {
        throw "$FilePath failed with exit code $exitCode."
    }
}

function Install-OokiVersionPayload {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [string] $PackageRoot,

        [Parameter(Mandatory)]
        [string] $VersionRoot
    )

    $source = Resolve-OokiExactPath -Path $PackageRoot `
        -Purpose 'Package root' -MustExist -PathType Directory
    $destination = Resolve-OokiExactPath -Path $VersionRoot `
        -Purpose 'Version root'
    if ([IO.Directory]::Exists($destination)) {
        $sourceChecksums = Join-Path $source 'checksums.txt'
        if (-not [IO.File]::Exists($sourceChecksums)) {
            throw 'The existing version cannot be compared because the source checksum manifest is missing.'
        }
        foreach ($line in Get-Content -LiteralPath $sourceChecksums) {
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }
            if ($line -notmatch '^([A-Fa-f0-9]{64})  ([^\r\n]+)$') {
                throw 'The release checksum manifest has an invalid line.'
            }
            $relative = $Matches[2]
            $existingFile = Join-Path $destination ($relative.Replace(
                '/',
                [IO.Path]::DirectorySeparatorChar))
            if (-not [IO.File]::Exists($existingFile) -or
                -not (Get-FileHash -LiteralPath $existingFile `
                    -Algorithm SHA256).Hash.Equals(
                        $Matches[1],
                        [StringComparison]::OrdinalIgnoreCase)) {
                throw 'The requested immutable version directory already exists but does not match the release package.'
            }
        }
        $sourcePrefix = $source.TrimEnd('\') + '\'
        $destinationPrefix = $destination.TrimEnd('\') + '\'
        $sourceFiles = @{}
        foreach ($sourceFile in Get-ChildItem -LiteralPath $source `
            -File -Recurse) {
            $relative = $sourceFile.FullName.Substring(
                $sourcePrefix.Length).Replace('\', '/')
            $sourceFiles[$relative] = (Get-FileHash `
                -LiteralPath $sourceFile.FullName `
                -Algorithm SHA256).Hash
        }
        $destinationFiles = @{}
        foreach ($destinationFile in Get-ChildItem `
            -LiteralPath $destination -File -Recurse) {
            $relative = $destinationFile.FullName.Substring(
                $destinationPrefix.Length).Replace('\', '/')
            $destinationFiles[$relative] = (Get-FileHash `
                -LiteralPath $destinationFile.FullName `
                -Algorithm SHA256).Hash
        }
        if ($sourceFiles.Count -ne $destinationFiles.Count) {
            throw 'The immutable version directory contains missing or extra files.'
        }
        foreach ($relative in $sourceFiles.Keys) {
            if (-not $destinationFiles.ContainsKey($relative) -or
                -not ([string] $destinationFiles[$relative]).Equals(
                    [string] $sourceFiles[$relative],
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw 'The immutable version directory contains missing, extra, or changed files.'
            }
        }
        return $destination
    }

    $reparsePoint = Get-ChildItem -LiteralPath $source -Force -Recurse |
        Where-Object {
            ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        } |
        Select-Object -First 1
    if ($null -ne $reparsePoint) {
        throw 'Release payloads may not contain reparse points or symbolic links.'
    }

    $parent = Split-Path -Parent $destination
    $staging = Join-Path $parent (
        '.staging-' + [IO.Path]::GetFileName($destination) + '-' +
        [Guid]::NewGuid().ToString('N'))
    if ($PSCmdlet.ShouldProcess($destination, 'Stage signed version payload')) {
        [IO.Directory]::CreateDirectory($parent) | Out-Null
        [IO.Directory]::CreateDirectory($staging) | Out-Null
        try {
            Get-ChildItem -LiteralPath $source -Force | ForEach-Object {
                Copy-Item -LiteralPath $_.FullName -Destination $staging `
                    -Recurse -Force
            }
            [IO.Directory]::Move($staging, $destination)
        } catch {
            if ([IO.Directory]::Exists($staging)) {
                [IO.Directory]::Delete($staging, $true)
            }
            throw
        }
    }

    return $destination
}

function Assert-OokiServiceName {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $ServiceName
    )

    if ($ServiceName -notmatch '^[A-Za-z][A-Za-z0-9._-]{0,63}$') {
        throw 'The Windows Service name contains unsupported characters.'
    }
    return $ServiceName
}

function Assert-OokiSchoolSubnet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]] $SchoolSubnet
    )

    if ($SchoolSubnet.Count -eq 0) {
        throw 'At least one explicit private school subnet is required.'
    }
    foreach ($item in $SchoolSubnet) {
        if ([string]::IsNullOrWhiteSpace($item) -or
            $item -in @('Any', '*', 'Internet', 'LocalSubnet')) {
            throw 'Firewall scope must use explicit private addresses or CIDR ranges.'
        }
        $parts = @($item -split '/', 2)
        $address = $null
        if (-not [Net.IPAddress]::TryParse($parts[0], [ref] $address)) {
            throw "The firewall scope is not an IP address or CIDR range: $item"
        }
        if ($parts.Count -eq 2) {
            $prefix = 0
            if (-not [int]::TryParse($parts[1], [ref] $prefix) -or
                ($address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork -and
                    ($prefix -lt 8 -or $prefix -gt 32)) -or
                ($address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetworkV6 -and
                    ($prefix -lt 7 -or $prefix -gt 128))) {
                throw "The firewall CIDR prefix is invalid: $item"
            }
        }
        $bytes = $address.GetAddressBytes()
        $private = if ($bytes.Length -eq 4) {
            $bytes[0] -eq 10 -or
            ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or
            ($bytes[0] -eq 192 -and $bytes[1] -eq 168)
        } else {
            ($bytes[0] -band 0xfe) -eq 0xfc
        }
        if (-not $private) {
            throw "The firewall scope must be private: $item"
        }
    }

    return $SchoolSubnet
}

function Set-OokiInstallAcl {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [string] $VersionRoot,

        [string] $ServiceName = 'OokiGrader.Host'
    )

    $root = Resolve-OokiExactPath -Path $VersionRoot `
        -Purpose 'Version root' -MustExist -PathType Directory
    Assert-OokiServiceName -ServiceName $ServiceName | Out-Null
    if ($PSCmdlet.ShouldProcess($root, 'Protect immutable application files')) {
        Invoke-OokiNative -FilePath "$env:SystemRoot\System32\icacls.exe" `
            -ArgumentList @(
                $root,
                '/inheritance:r',
                '/grant:r',
                'SYSTEM:(OI)(CI)F',
                'BUILTIN\Administrators:(OI)(CI)F',
                'BUILTIN\Users:(OI)(CI)RX',
                "NT SERVICE\${ServiceName}:(OI)(CI)RX",
                '/T',
                '/C'
            )
    }
}

function Set-OokiDataAcl {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [string] $DataRoot,

        [string] $ServiceName = 'OokiGrader.Host'
    )

    $root = Assert-OokiDataRoot -DataRoot $DataRoot
    Assert-OokiServiceName -ServiceName $ServiceName | Out-Null
    $knownDirectories = @(
        $root,
        (Join-Path $root 'objects'),
        (Join-Path $root 'incoming'),
        (Join-Path $root 'reports'),
        (Join-Path $root 'secrets'),
        (Join-Path $root 'data-protection-keys'),
        (Join-Path $root 'certificates'),
        (Join-Path $root 'configuration'),
        (Join-Path $root 'operations')
    )
    $serviceAccount = "NT SERVICE\$ServiceName"
    foreach ($directory in $knownDirectories) {
        if ($PSCmdlet.ShouldProcess($directory, 'Create and secure exact data directory')) {
            [IO.Directory]::CreateDirectory($directory) | Out-Null
            Invoke-OokiNative -FilePath "$env:SystemRoot\System32\icacls.exe" `
                -ArgumentList @(
                    $directory,
                    '/inheritance:r',
                    '/grant:r',
                    'SYSTEM:(OI)(CI)F',
                    'BUILTIN\Administrators:(OI)(CI)F',
                    "${serviceAccount}:(OI)(CI)M"
                )
        }
    }
}

function Install-OokiHostCertificate {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [string] $SourcePath,

        [Parameter(Mandatory)]
        [string] $DataRoot,

        [Parameter(Mandatory)]
        [string] $DnsName,

        [string] $ServiceName = 'OokiGrader.Host'
    )

    $source = Resolve-OokiExactPath -Path $SourcePath `
        -Purpose 'Host certificate' -MustExist -PathType File
    $extension = [IO.Path]::GetExtension($source)
    if ($extension -notin @('.pfx', '.p12')) {
        throw 'The host certificate must be an empty-password PKCS#12 file (.pfx or .p12).'
    }
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $source,
        '',
        [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    try {
        if (-not $certificate.HasPrivateKey) {
            throw 'The host certificate does not contain an accessible private key.'
        }
        $now = [DateTimeOffset]::UtcNow.UtcDateTime
        if ($certificate.NotBefore.ToUniversalTime() -gt $now.AddMinutes(5) -or
            $certificate.NotAfter.ToUniversalTime() -le $now.AddDays(30)) {
            throw 'The host certificate is not currently usable for at least 30 days.'
        }
        $certificateDns = $certificate.GetNameInfo(
            [Security.Cryptography.X509Certificates.X509NameType]::DnsName,
            $false)
        if (-not $certificateDns.Equals(
            $DnsName,
            [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The host certificate DNS identity does not match the configured host name.'
        }
        $serverAuthenticationOid = '1.3.6.1.5.5.7.3.1'
        $eku = $certificate.Extensions |
            Where-Object {
                $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]
            } |
            Select-Object -First 1
        if ($null -eq $eku -or
            $null -eq ($eku.EnhancedKeyUsages |
                Where-Object Value -eq $serverAuthenticationOid |
                Select-Object -First 1)) {
            throw 'The host certificate is not valid for TLS server authentication.'
        }
    } finally {
        $certificate.Dispose()
    }

    $root = Assert-OokiDataRoot -DataRoot $DataRoot
    $certificateRoot = Join-Path $root 'certificates'
    $destination = Join-Path $certificateRoot 'ooki-grader-host.pfx'
    if ($PSCmdlet.ShouldProcess(
        $destination,
        'Install validated host certificate into persistent protected storage')) {
        [IO.Directory]::CreateDirectory($certificateRoot) | Out-Null
        $temporary = Join-Path $certificateRoot (
            '.host-' + [Guid]::NewGuid().ToString('N') + '.tmp')
        try {
            [IO.File]::Copy($source, $temporary, $false)
            Move-Item -LiteralPath $temporary -Destination $destination -Force
        } finally {
            if ([IO.File]::Exists($temporary)) {
                [IO.File]::Delete($temporary)
            }
        }
        Set-OokiCertificateAcl -CertificatePath $destination `
            -ServiceName $ServiceName -Confirm:$false
    }

    return $destination
}

function Set-OokiCertificateAcl {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [string] $CertificatePath,

        [string] $ServiceName = 'OokiGrader.Host'
    )

    Assert-OokiServiceName -ServiceName $ServiceName | Out-Null
    $path = Resolve-OokiExactPath -Path $CertificatePath `
        -Purpose 'Host certificate' -MustExist -PathType File
    if ($PSCmdlet.ShouldProcess($path, 'Restrict host certificate ACL')) {
        Invoke-OokiNative -FilePath "$env:SystemRoot\System32\icacls.exe" `
            -ArgumentList @(
                $path,
                '/inheritance:r',
                '/grant:r',
                'SYSTEM:F',
                'BUILTIN\Administrators:F',
                "NT SERVICE\${ServiceName}:R"
            )
    }
}

function Set-OokiWindowsService {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [string] $ExecutablePath,

        [Parameter(Mandatory)]
        [string] $ContentRoot,

        [Parameter(Mandatory)]
        [string] $ConfigurationPath,

        [string] $ServiceName = 'OokiGrader.Host'
    )

    Assert-OokiServiceName -ServiceName $ServiceName | Out-Null
    $executable = Resolve-OokiExactPath -Path $ExecutablePath `
        -Purpose 'Host executable' -MustExist -PathType File
    $content = Resolve-OokiExactPath -Path $ContentRoot `
        -Purpose 'Version content root' -MustExist -PathType Directory
    $configuration = Resolve-OokiExactPath -Path $ConfigurationPath `
        -Purpose 'Persistent production configuration'
    $binaryCommand = '"{0}" --environment Production --contentRoot "{1}" --ooki-config "{2}"' -f `
        $executable, $content, $configuration
    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Configure delayed automatic Windows Service')) {
        if ($null -eq $existing) {
            Invoke-OokiNative -FilePath "$env:SystemRoot\System32\sc.exe" `
                -ArgumentList @(
                    'create',
                    $ServiceName,
                    'binPath=',
                    $binaryCommand,
                    'start=',
                    'delayed-auto',
                    'obj=',
                    "NT SERVICE\$ServiceName",
                    'DisplayName=',
                    'Ooki Grader'
                )
        } else {
            Invoke-OokiNative -FilePath "$env:SystemRoot\System32\sc.exe" `
                -ArgumentList @(
                    'config',
                    $ServiceName,
                    'binPath=',
                    $binaryCommand,
                    'start=',
                    'delayed-auto',
                    'obj=',
                    "NT SERVICE\$ServiceName",
                    'DisplayName=',
                    'Ooki Grader'
                )
        }

        Invoke-OokiNative -FilePath "$env:SystemRoot\System32\sc.exe" `
            -ArgumentList @(
                'description',
                $ServiceName,
                'Ooki Grader private LAN grading service'
            )
        Invoke-OokiNative -FilePath "$env:SystemRoot\System32\sc.exe" `
            -ArgumentList @('sidtype', $ServiceName, 'unrestricted')
        Invoke-OokiNative -FilePath "$env:SystemRoot\System32\sc.exe" `
            -ArgumentList @(
                'failure',
                $ServiceName,
                'reset=',
                '86400',
                'actions=',
                'restart/10000/restart/60000/none/0'
            )
        Invoke-OokiNative -FilePath "$env:SystemRoot\System32\sc.exe" `
            -ArgumentList @('failureflag', $ServiceName, '1')
    }
}

function Set-OokiFirewallRule {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [ValidateRange(1, 65535)]
        [int] $Port,

        [Parameter(Mandatory)]
        [string[]] $RemoteAddress,

        [string] $RuleName = 'Ooki Grader HTTPS'
    )

    Assert-OokiSchoolSubnet -SchoolSubnet $RemoteAddress | Out-Null

    $existing = Get-NetFirewallRule -DisplayName $RuleName `
        -ErrorAction SilentlyContinue
    if ($PSCmdlet.ShouldProcess($RuleName, 'Create or update private HTTPS firewall rule')) {
        if ($null -eq $existing) {
            New-NetFirewallRule -DisplayName $RuleName `
                -Direction Inbound -Action Allow -Enabled True `
                -Profile Private -Protocol TCP -LocalPort $Port `
                -RemoteAddress $RemoteAddress | Out-Null
        } else {
            $existing | Set-NetFirewallRule -Direction Inbound `
                -Action Allow -Enabled True -Profile Private | Out-Null
            $existing | Set-NetFirewallPortFilter -Protocol TCP `
                -LocalPort $Port | Out-Null
            $existing | Set-NetFirewallAddressFilter `
                -RemoteAddress $RemoteAddress | Out-Null
        }
    }
}

function Write-OokiJsonFile {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [object] $Value
    )

    $target = Resolve-OokiExactPath -Path $Path `
        -Purpose 'Configuration file'
    if ($PSCmdlet.ShouldProcess($target, 'Write sanitized service configuration')) {
        $parent = Split-Path -Parent $target
        [IO.Directory]::CreateDirectory($parent) | Out-Null
        $temporary = Join-Path $parent (
            '.' + [IO.Path]::GetFileName($target) + '.' +
            [Guid]::NewGuid().ToString('N') + '.tmp')
        try {
            $Value | ConvertTo-Json -Depth 12 |
                Set-Content -LiteralPath $temporary -Encoding UTF8
            Move-Item -LiteralPath $temporary -Destination $target -Force
        } finally {
            if ([IO.File]::Exists($temporary)) {
                [IO.File]::Delete($temporary)
            }
        }
    }
}

function Get-OokiServiceImagePath {
    [CmdletBinding()]
    param(
        [string] $ServiceName = 'OokiGrader.Host'
    )

    $escaped = $ServiceName.Replace("'", "''")
    $service = Get-CimInstance Win32_Service `
        -Filter "Name='$escaped'" -ErrorAction Stop
    return $service.PathName
}

function Get-OokiServiceExecutablePath {
    [CmdletBinding()]
    param(
        [string] $ServiceName = 'OokiGrader.Host'
    )

    Assert-OokiServiceName -ServiceName $ServiceName | Out-Null
    $imagePath = Get-OokiServiceImagePath -ServiceName $ServiceName
    if ($imagePath -match '^\s*"([^"]+)"') {
        return [IO.Path]::GetFullPath($Matches[1])
    }
    if ($imagePath -match '^\s*([^\s]+)') {
        return [IO.Path]::GetFullPath($Matches[1])
    }
    throw 'The Windows Service image path is invalid.'
}

function Read-OokiInstallationManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $DataRoot
    )

    $root = Assert-OokiDataRoot -DataRoot $DataRoot
    $path = Join-Path (Join-Path $root 'operations') 'installation.json'
    if (-not [IO.File]::Exists($path)) {
        return $null
    }
    try {
        $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    } catch {
        throw 'The persistent installation manifest is invalid.'
    }
    if ($manifest.schema -ne 'ooki-installation/v1' -or
        $manifest.product -ne 'Ooki Grader') {
        throw 'The persistent installation manifest has an unsupported identity.'
    }
    return $manifest
}

function Write-OokiInstallationManifest {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [string] $DataRoot,

        [Parameter(Mandatory)]
        [string] $Version,

        [Parameter(Mandatory)]
        [string] $InstallRoot,

        [Parameter(Mandatory)]
        [string] $ServiceName,

        [Parameter(Mandatory)]
        [string] $DnsName,

        [Parameter(Mandatory)]
        [int] $HttpsPort,

        [Parameter(Mandatory)]
        [string] $CertificatePath,

        [Parameter(Mandatory)]
        [string] $ConfigurationPath,

        [string] $ExpectedSignerThumbprint
    )

    $root = Assert-OokiDataRoot -DataRoot $DataRoot
    Assert-OokiServiceName -ServiceName $ServiceName | Out-Null
    $path = Join-Path (Join-Path $root 'operations') 'installation.json'
    $value = [ordered]@{
        schema = 'ooki-installation/v1'
        product = 'Ooki Grader'
        version = $Version
        installRoot = $InstallRoot
        dataRoot = $root
        serviceName = $ServiceName
        dnsName = $DnsName
        httpsPort = $HttpsPort
        certificatePath = $CertificatePath
        configurationPath = $ConfigurationPath
        expectedSignerThumbprint = $ExpectedSignerThumbprint
        updatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    }
    if ($PSCmdlet.ShouldProcess(
        $path,
        'Write persistent Ooki Grader installation identity')) {
        Write-OokiJsonFile -Path $path -Value $value -Confirm:$false
    }
    return $path
}

function Invoke-OokiToolJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $ToolPath,

        [Parameter(Mandatory)]
        [string[]] $Arguments,

        [switch] $AllowCheckFailure
    )

    $tool = Resolve-OokiExactPath -Path $ToolPath `
        -Purpose 'OokiGrader.Tool executable' -MustExist -PathType File
    $allArguments = @($Arguments)
    if ($allArguments -notcontains '--json') {
        $allArguments += '--json'
    }
    $output = & $tool @allArguments 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -and -not $AllowCheckFailure) {
        throw "OokiGrader.Tool reported exit code $exitCode."
    }
    try {
        return $output | ConvertFrom-Json
    } catch {
        throw 'OokiGrader.Tool returned invalid diagnostic JSON.'
    }
}

function New-OokiOperationMarker {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [string] $DataRoot,

        [Parameter(Mandatory)]
        [ValidateSet('migration.in-progress', 'restore.in-progress')]
        [string] $Name
    )

    $root = Assert-OokiDataRoot -DataRoot $DataRoot
    $operations = Join-Path $root 'operations'
    $marker = Join-Path $operations $Name
    if ($PSCmdlet.ShouldProcess($marker, 'Create maintenance operation marker')) {
        [IO.Directory]::CreateDirectory($operations) | Out-Null
        [DateTimeOffset]::UtcNow.ToString('O') |
            Set-Content -LiteralPath $marker -Encoding ASCII
    }
    return $marker
}

function Remove-OokiOperationMarker {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [string] $MarkerPath
    )

    $marker = Resolve-OokiExactPath -Path $MarkerPath `
        -Purpose 'Operation marker'
    if ([IO.File]::Exists($marker) -and
        $PSCmdlet.ShouldProcess($marker, 'Remove completed operation marker')) {
        [IO.File]::Delete($marker)
    }
}

function Wait-OokiService {
    [CmdletBinding()]
    param(
        [string] $ServiceName = 'OokiGrader.Host',

        [ValidateRange(5, 300)]
        [int] $TimeoutSeconds = 60
    )

    $service = Get-Service -Name $ServiceName -ErrorAction Stop
    $service.WaitForStatus(
        [System.ServiceProcess.ServiceControllerStatus]::Running,
        [TimeSpan]::FromSeconds($TimeoutSeconds))
}

function Test-OokiReadyEndpoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [Uri] $Uri,

        [ValidateRange(5, 300)]
        [int] $TimeoutSeconds = 60
    )

    if ($Uri.Scheme -ne 'https') {
        throw 'Readiness checks require HTTPS.'
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $response = Invoke-WebRequest -Uri $Uri -Method Get `
                -UseBasicParsing -TimeoutSec 10
            if ($response.StatusCode -eq 200) {
                try {
                    $body = $response.Content | ConvertFrom-Json
                    if ($body.state -eq 'healthy' -and
                        $body.database -eq 'healthy' -and
                        $body.schema -eq 'healthy' -and
                        $body.storage -eq 'healthy') {
                        return $true
                    }
                } catch {
                    # A generic HTTPS 200 is not sufficient readiness evidence.
                }
            }
        } catch {
            Start-Sleep -Seconds 2
        }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    return $false
}

function Write-OokiWindowsEvent {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [ValidateRange(1, 65535)]
        [int] $EventId,

        [Parameter(Mandatory)]
        [ValidateSet('Information', 'Warning', 'Error')]
        [string] $EntryType,

        [Parameter(Mandatory)]
        [ValidateLength(1, 30000)]
        [string] $Message,

        [string] $Source = 'Ooki Grader'
    )

    if ($PSCmdlet.ShouldProcess(
        'Windows Application event log',
        "Write Ooki Grader event $EventId")) {
        if (-not [Diagnostics.EventLog]::SourceExists($Source)) {
            New-EventLog -LogName Application -Source $Source
        }
        Write-EventLog -LogName Application -Source $Source `
            -EventId $EventId -EntryType $EntryType -Message $Message
    }
}

Export-ModuleMember -Function @(
    'Assert-OokiWindows',
    'Assert-OokiAdministrator',
    'Resolve-OokiExactPath',
    'Assert-OokiDisjointPaths',
    'Assert-OokiInstallRoot',
    'Assert-OokiDataRoot',
    'Assert-OokiAuthenticodeSignature',
    'Assert-OokiReleasePackage',
    'Invoke-OokiNative',
    'Install-OokiVersionPayload',
    'Assert-OokiServiceName',
    'Assert-OokiSchoolSubnet',
    'Set-OokiInstallAcl',
    'Set-OokiDataAcl',
    'Install-OokiHostCertificate',
    'Set-OokiCertificateAcl',
    'Set-OokiWindowsService',
    'Set-OokiFirewallRule',
    'Write-OokiJsonFile',
    'Get-OokiServiceImagePath',
    'Get-OokiServiceExecutablePath',
    'Read-OokiInstallationManifest',
    'Write-OokiInstallationManifest',
    'Invoke-OokiToolJson',
    'New-OokiOperationMarker',
    'Remove-OokiOperationMarker',
    'Wait-OokiService',
    'Test-OokiReadyEndpoint',
    'Write-OokiWindowsEvent'
)
