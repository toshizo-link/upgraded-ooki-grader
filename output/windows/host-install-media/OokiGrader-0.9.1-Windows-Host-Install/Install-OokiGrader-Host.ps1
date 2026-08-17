#requires -Version 5.1
#requires -RunAsAdministrator

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$version = '0.9.1'
$archiveName = "OokiGrader-$version-win-x64.zip"
$mediaRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$archivePath = Join-Path $mediaRoot $archiveName
$checksumPath = "$archivePath.sha256"
$stagingRoot = 'C:\OokiGrader-Setup'
$packageRoot = Join-Path $stagingRoot "OokiGrader-$version-win-x64"
$powerShellInstallerName = 'PowerShell-7.6.5-win-x64.msi'
$powerShellInstaller = Join-Path (
    Join-Path $mediaRoot 'Prerequisites') $powerShellInstallerName
$powerShellInstallerChecksum = "$powerShellInstaller.sha256"

function Find-PowerShell7 {
    $command = Get-Command pwsh.exe -CommandType Application `
        -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) {
        return $command.Source
    }
    $standardPath = Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'
    if ([IO.File]::Exists($standardPath)) {
        return $standardPath
    }
    return $null
}

foreach ($requiredFile in @($archivePath, $checksumPath)) {
    if (-not [IO.File]::Exists($requiredFile)) {
        throw "必要なファイルがありません: $requiredFile"
    }
}

Write-Host '1/4 インストールメディアのSHA-256を確認しています…'
$checksumText = (Get-Content -LiteralPath $checksumPath -Raw).Trim()
if ($checksumText -notmatch '^(?<hash>[A-Fa-f0-9]{64})\s+') {
    throw 'SHA-256ファイルの形式が正しくありません。'
}
$expectedHash = $Matches.hash
$actualHash = (Get-FileHash -LiteralPath $archivePath `
    -Algorithm SHA256).Hash
if (-not $actualHash.Equals(
    $expectedHash,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ZIPのSHA-256が一致しません。コピーし直してください。'
}

Write-Host '2/4 64-bit PowerShell 7.4以降を確認しています…'
$pwsh = Find-PowerShell7
if ($null -eq $pwsh) {
    foreach ($requiredPrerequisite in @(
        $powerShellInstaller,
        $powerShellInstallerChecksum
    )) {
        if (-not [IO.File]::Exists($requiredPrerequisite)) {
            throw "PowerShell 7がなく、同梱インストーラーもありません: $requiredPrerequisite"
        }
    }
    $installerChecksumText = (
        Get-Content -LiteralPath $powerShellInstallerChecksum -Raw
    ).Trim()
    if ($installerChecksumText -notmatch `
        '^(?<hash>[A-Fa-f0-9]{64})\s+') {
        throw 'PowerShell MSIのSHA-256ファイル形式が正しくありません。'
    }
    $expectedInstallerHash = $Matches.hash
    $actualInstallerHash = (
        Get-FileHash -LiteralPath $powerShellInstaller -Algorithm SHA256
    ).Hash
    if (-not $actualInstallerHash.Equals(
        $expectedInstallerHash,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw 'PowerShell MSIのSHA-256が一致しません。実行を中止します。'
    }
    $installerSignature = Get-AuthenticodeSignature `
        -LiteralPath $powerShellInstaller
    if ($installerSignature.Status -ne 'Valid' -or
        $null -eq $installerSignature.SignerCertificate -or
        $installerSignature.SignerCertificate.Subject -notmatch `
            'O=Microsoft Corporation') {
        throw 'PowerShell MSIのMicrosoftデジタル署名を確認できません。実行を中止します。'
    }
    Write-Host 'Microsoft公式PowerShell 7インストーラーを開きます。画面に従ってインストールしてください。'
    $msiProcess = Start-Process `
        -FilePath "$env:SystemRoot\System32\msiexec.exe" `
        -ArgumentList @('/i', "`"$powerShellInstaller`"") `
        -Wait -PassThru
    if ($msiProcess.ExitCode -notin @(0, 3010)) {
        throw "PowerShell 7のインストールが終了コード $($msiProcess.ExitCode) で停止しました。"
    }
    $pwsh = Find-PowerShell7
    if ($null -eq $pwsh) {
        throw 'PowerShell 7のインストール後もpwsh.exeが見つかりません。Windowsを再起動してから、もう一度実行してください。'
    }
}
$pwshStatus = & $pwsh -NoLogo -NoProfile -NonInteractive `
    -Command '[pscustomobject]@{Version=$PSVersionTable.PSVersion.ToString();Is64Bit=[Environment]::Is64BitProcess}|ConvertTo-Json -Compress'
if ($LASTEXITCODE -ne 0) {
    throw 'PowerShell 7の確認に失敗しました。'
}
$pwshInfo = $pwshStatus | ConvertFrom-Json
if ([version] $pwshInfo.Version -lt [version] '7.4' -or
    -not [bool] $pwshInfo.Is64Bit) {
    throw '64-bit PowerShell 7.4以降が必要です。'
}

Write-Host '3/4 Ooki Graderの完全パッケージを展開しています…'
if ([IO.Directory]::Exists($packageRoot)) {
    throw "$packageRoot は既に存在します。以前の作業結果を確認してから、別名へ退避してください。"
}
[IO.Directory]::CreateDirectory($stagingRoot) | Out-Null
Expand-Archive -LiteralPath $archivePath -DestinationPath $stagingRoot

foreach ($relativePath in @(
    'release-inventory.json',
    'checksums.txt',
    'OokiGrader.Host.exe',
    'OokiGrader.Tool.exe',
    'Install-OokiGraderOnSite.ps1'
)) {
    $requiredPath = Join-Path $packageRoot $relativePath
    if (-not [IO.File]::Exists($requiredPath)) {
        throw "展開したパッケージに必要なファイルがありません: $relativePath"
    }
}

Write-Host '4/4 対話式のホストPCセットアップを開始します…'
Write-Host '表示された値を確認できない場合は、INSTALLを入力せずCtrl+Cで中止してください。'
$backupRoot = Read-Host `
    '暗号化済みバックアップ保存先（例 E:\OokiGraderBackup、後で設定する場合はEnter）'
$onSiteArguments = @(
    '-NoLogo',
    '-NoProfile',
    '-File',
    (Join-Path $packageRoot 'Install-OokiGraderOnSite.ps1'),
    '-PackageRoot',
    $packageRoot
)
if (-not [string]::IsNullOrWhiteSpace($backupRoot)) {
    $onSiteArguments += @('-BackupRoot', $backupRoot.Trim())
}
& $pwsh @onSiteArguments
if ($LASTEXITCODE -ne 0) {
    throw "Ooki Graderセットアップが終了コード $LASTEXITCODE で停止しました。"
}

Write-Host 'Ooki GraderのホストPCセットアップが正常に完了しました。'
