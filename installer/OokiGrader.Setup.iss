#ifndef OokiPackageRoot
  #error OokiPackageRoot must identify the assembled release package.
#endif
#ifndef OokiVersion
  #error OokiVersion must be a canonical semantic version.
#endif
#ifndef OokiOutputRoot
  #error OokiOutputRoot must identify the installer output directory.
#endif
#ifndef OokiNumericVersion
  #error OokiNumericVersion must be a four-part Windows file version.
#endif
#ifndef OokiExpectedSignerThumbprint
  #define OokiExpectedSignerThumbprint ""
#endif
#ifndef OokiAllowUnsigned
  #define OokiAllowUnsigned "0"
#endif
#ifndef OokiSignOutput
  #define OokiSignOutput "0"
#endif

[Setup]
AppId={{2F0AB029-63F8-4C8B-A86D-369E807C529D}
AppName=Ooki Grader
AppVersion={#OokiVersion}
AppVerName=Ooki Grader {#OokiVersion}
AppPublisher=Ooki Grader
VersionInfoDescription=Ooki Grader Windows Installer
VersionInfoProductName=Ooki Grader
VersionInfoProductVersion={#OokiNumericVersion}
DefaultDirName={autopf}\Ooki Grader
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputDir={#OokiOutputRoot}
OutputBaseFilename=OokiGrader-Setup-{#OokiVersion}-x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
SetupLogging=yes
CloseApplications=no
RestartApplications=no
UsePreviousAppDir=no
Uninstallable=yes
UninstallDisplayName=Ooki Grader
UninstallDisplayIcon={app}\versions\{#OokiVersion}\OokiGrader.Host.exe
#if OokiSignOutput == "1"
SignTool=ooki
SignedUninstaller=yes
#else
SignedUninstaller=no
#endif

[Files]
Source: "{#OokiPackageRoot}\*"; DestDir: "{tmp}\OokiGraderPackage"; Flags: recursesubdirs createallsubdirs deleteafterinstall
Source: "{#OokiPackageRoot}\Install-OokiGrader.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "{#OokiPackageRoot}\Uninstall-OokiGrader.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "{#OokiPackageRoot}\Repair-OokiGrader.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "{#OokiPackageRoot}\Test-OokiGraderHealth.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "{#OokiPackageRoot}\Test-OokiGraderPreflight.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "{#OokiPackageRoot}\Upgrade-OokiGrader.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "{#OokiPackageRoot}\Restore-OokiGrader.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "{#OokiPackageRoot}\New-OokiGraderCertificate.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "{#OokiPackageRoot}\Install-OokiGraderOnSite.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "{#OokiPackageRoot}\New-OokiGraderPeerTrustPackage.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "{#OokiPackageRoot}\Install-OokiGraderPeerTrust.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "{#OokiPackageRoot}\OokiGrader.Windows.psm1"; DestDir: "{app}\installer"; Flags: ignoreversion

[Registry]
Root: HKLM; Subkey: "Software\OokiGrader"; ValueType: string; ValueName: "InstalledVersion"; ValueData: "{#OokiVersion}"; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\OokiGrader"; ValueType: string; ValueName: "InstallRoot"; ValueData: "{app}"
Root: HKLM; Subkey: "Software\OokiGrader"; ValueType: string; ValueName: "DataRoot"; ValueData: "{code:GetDataRoot}"
Root: HKLM; Subkey: "Software\OokiGrader"; ValueType: string; ValueName: "DnsName"; ValueData: "{code:GetDnsName}"
Root: HKLM; Subkey: "Software\OokiGrader"; ValueType: string; ValueName: "HttpsPort"; ValueData: "{code:GetHttpsPort}"

[Icons]
Name: "{commonprograms}\Ooki Grader\Ooki Grader を開く"; Filename: "{code:GetApplicationUrl}"
Name: "{commonprograms}\Ooki Grader\状態を確認"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "{code:GetHealthParameters}"; WorkingDir: "{app}\installer"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\versions"

[Code]
const
  ProductRegistryKey = 'Software\OokiGrader';
  ExpectedSignerThumbprint = '{#OokiExpectedSignerThumbprint}';
  AllowUnsignedDevelopmentBuild = {#OokiAllowUnsigned};

var
  NetworkPage: TInputQueryWizardPage;

function PowerShellPath: string;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

function GetDataRoot(Param: string): string;
begin
  if not RegQueryStringValue(HKLM, ProductRegistryKey, 'DataRoot', Result) then
    Result := ExpandConstant('{commonappdata}\OokiGrader\Data');
end;

function GetDnsName(Param: string): string;
begin
  if NetworkPage <> nil then
    Result := NetworkPage.Values[0]
  else if not RegQueryStringValue(HKLM, ProductRegistryKey, 'DnsName', Result) then
    Result := '';
end;

function GetHttpsPort(Param: string): string;
begin
  if NetworkPage <> nil then
    Result := NetworkPage.Values[1]
  else if not RegQueryStringValue(HKLM, ProductRegistryKey, 'HttpsPort', Result) then
    Result := '443';
end;

function GetApplicationUrl(Param: string): string;
begin
  if GetHttpsPort('') = '443' then
    Result := 'https://' + GetDnsName('') + '/'
  else
    Result := 'https://' + GetDnsName('') + ':' + GetHttpsPort('') + '/';
end;

function GetExecutionPolicy: string;
begin
  { The signed Setup container protects the extracted script bytes, and the
    script revalidates the complete payload and approved signer before any
    mutation. AllSigned is intentionally not used here: on a clean machine it
    prompts for an otherwise valid but not-yet-classified publisher, while the
    installer invokes PowerShell noninteractively. }
  Result := 'Bypass';
end;

function GetCommonPowerShellParameters: string;
begin
  Result := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy ' +
    GetExecutionPolicy();
end;

function GetHealthParameters(Param: string): string;
begin
  Result := GetCommonPowerShellParameters() +
    ' -File ' + AddQuotes(ExpandConstant('{app}\installer\Test-OokiGraderHealth.ps1')) +
    ' -ToolPath ' + AddQuotes(ExpandConstant('{app}\versions\{#OokiVersion}\OokiGrader.Tool.exe')) +
    ' -DatabasePath ' + AddQuotes(AddBackslash(GetDataRoot('')) + 'ooki-grader.db') +
    ' -DataRoot ' + AddQuotes(GetDataRoot('')) +
    ' -ContentRoot ' + AddQuotes(AddBackslash(GetDataRoot('')) + 'objects') +
    ' -ReadyUri ' + AddQuotes(GetApplicationUrl('') + 'health/ready');
end;

function GetUninstallParameters(Param: string): string;
begin
  Result := GetCommonPowerShellParameters() +
    ' -File ' + AddQuotes(ExpandConstant('{app}\installer\Uninstall-OokiGrader.ps1')) +
    ' -InstallRoot ' + AddQuotes(ExpandConstant('{app}')) +
    ' -DataRoot ' + AddQuotes(GetDataRoot('')) +
    ' -OfflineConfirmed -InstallerManagedApplicationRemoval';
end;

function BuildInstallParameters: string;
begin
  Result := GetCommonPowerShellParameters() +
    ' -File ' + AddQuotes(ExpandConstant('{tmp}\OokiGraderPackage\Install-OokiGraderOnSite.ps1')) +
    ' -PackageRoot ' + AddQuotes(ExpandConstant('{tmp}\OokiGraderPackage')) +
    ' -DataRoot ' + AddQuotes(GetDataRoot('')) +
    ' -DnsName ' + AddQuotes(GetDnsName('')) +
    ' -InstallRoot ' + AddQuotes(ExpandConstant('{app}')) +
    ' -HttpsPort ' + GetHttpsPort('') +
    ' -ExpectedSignerThumbprint ' + AddQuotes(ExpectedSignerThumbprint) +
    ' -InstallationConfirmed' +
    ' -NonInteractive';
  if AllowUnsignedDevelopmentBuild = 1 then
    Result := Result + ' -AcceptChecksumVerifiedUnsignedOnSitePackage';
end;

function InitializeSetup: Boolean;
var
  ExistingVersion: string;
begin
  Result := True;
  if WizardSilent then
  begin
    MsgBox(
      '無人セットアップには対応していません。通常の対話セットアップを実行してください。',
      mbError, MB_OK);
    Result := False;
    Exit;
  end;
  if not FileExists(PowerShellPath()) then
  begin
    MsgBox(
      'Windows 標準 PowerShell 5.1 が見つかりません。Windows コンポーネントを修復してから、もう一度実行してください。',
      mbError, MB_OK);
    Result := False;
    Exit;
  end;
  if RegQueryStringValue(
      HKLM, ProductRegistryKey, 'InstalledVersion', ExistingVersion) and
      (CompareText(ExistingVersion, '{#OokiVersion}') <> 0) then
  begin
    MsgBox(
      '別の Ooki Grader バージョン (' + ExistingVersion +
      ') がインストールされています。データ移行と検証済みバックアップを伴う Upgrade-OokiGrader.ps1 を使用してください。',
      mbError, MB_OK);
    Result := False;
  end;
end;

procedure InitializeWizard;
var
  ExistingValue: string;
  WindowsVersion: TWindowsVersion;
begin
  GetWindowsVersionEx(WindowsVersion);
  if WindowsVersion.Build < 22000 then
    MsgBox(
      'Windows 11 Pro の現行サポート対象ビルドを推奨します。現在の Windows でもセットアップは続行できますが、性能、安定性、サポート状況を確認してください。',
      mbInformation, MB_OK);

  NetworkPage := CreateInputQueryPage(
    wpSelectDir,
    '校内ネットワーク',
    'Ooki Grader の校内 URL を確認してください。',
    'DNS 名と HTTPS ポートは証明書・ブラウザー URL と一致する必要があります。');
  NetworkPage.Add('DNS 名:', False);
  NetworkPage.Add('HTTPS ポート:', False);
  if not RegQueryStringValue(HKLM, ProductRegistryKey, 'DnsName', ExistingValue) then
    ExistingValue := 'ooki-grader.test';
  NetworkPage.Values[0] := ExistingValue;
  if not RegQueryStringValue(HKLM, ProductRegistryKey, 'HttpsPort', ExistingValue) then
    ExistingValue := '443';
  NetworkPage.Values[1] := ExistingValue;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Port: Integer;
begin
  Result := True;
  if CurPageID = NetworkPage.ID then
  begin
    Port := StrToIntDef(GetHttpsPort(''), 0);
    if (Trim(GetDnsName('')) = '') or
       (Pos(' ', GetDnsName('')) > 0) or
       (Port < 1) or (Port > 65535) then
    begin
      MsgBox('DNS 名と1〜65535 の HTTPS ポートを確認してください。', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ExitCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    WizardForm.StatusLabel.Caption := 'Ooki Grader サービスを構成し、起動確認をしています…';
    if (not Exec(
        PowerShellPath(),
        BuildInstallParameters(),
        ExpandConstant('{tmp}\OokiGraderPackage'),
        SW_HIDE,
        ewWaitUntilTerminated,
        ExitCode)) or (ExitCode <> 0) then
      RaiseException(
        'Ooki Grader の構成または起動確認に失敗しました。セットアップログと Windows イベントログを確認してください。終了コード: ' +
        IntToStr(ExitCode));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ExitCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    if (not Exec(
        PowerShellPath(),
        GetUninstallParameters(''),
        ExpandConstant('{app}\installer'),
        SW_HIDE,
        ewWaitUntilTerminated,
        ExitCode)) or (ExitCode <> 0) then
      RaiseException(
        '安全なアプリ退避またはサービス解除に失敗したため、アンインストールを中止しました。終了コード: ' +
        IntToStr(ExitCode));
  end;
end;
