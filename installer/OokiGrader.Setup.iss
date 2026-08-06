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
  #define OokiAllowUnsigned 0
#endif
#ifndef OokiSignOutput
  #define OokiSignOutput 0
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
MinVersion=10.0.22000
SetupLogging=yes
CloseApplications=no
RestartApplications=no
UsePreviousAppDir=no
Uninstallable=yes
UninstallDisplayName=Ooki Grader
UninstallDisplayIcon={app}\versions\{#OokiVersion}\OokiGrader.Host.exe
#if OokiSignOutput == 1
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
Source: "{#OokiPackageRoot}\Install-OokiGraderPeerTrust.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "{#OokiPackageRoot}\OokiGrader.Windows.psm1"; DestDir: "{app}\installer"; Flags: ignoreversion

[Registry]
Root: HKLM; Subkey: "Software\OokiGrader"; ValueType: string; ValueName: "InstalledVersion"; ValueData: "{#OokiVersion}"; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\OokiGrader"; ValueType: string; ValueName: "InstallRoot"; ValueData: "{app}"
Root: HKLM; Subkey: "Software\OokiGrader"; ValueType: string; ValueName: "DataRoot"; ValueData: "{code:GetDataRoot}"
Root: HKLM; Subkey: "Software\OokiGrader"; ValueType: string; ValueName: "DnsName"; ValueData: "{code:GetDnsName}"
Root: HKLM; Subkey: "Software\OokiGrader"; ValueType: string; ValueName: "HttpsPort"; ValueData: "{code:GetHttpsPort}"

[Icons]
Name: "{commonprograms}\Ooki Grader\Ooki Grader を開く"; Filename: "{code:GetApplicationUrl}"; Flags: shellexec
Name: "{commonprograms}\Ooki Grader\状態を確認"; Filename: "{autopf}\PowerShell\7\pwsh.exe"; Parameters: "{code:GetHealthParameters}"; WorkingDir: "{app}\installer"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\versions"

[Code]
const
  ProductRegistryKey = 'Software\OokiGrader';
  ExpectedSignerThumbprint = '{#OokiExpectedSignerThumbprint}';
  AllowUnsignedDevelopmentBuild = {#OokiAllowUnsigned};

var
  DataPage: TInputDirWizardPage;
  NetworkPage: TInputQueryWizardPage;
  CertificatePage: TInputFileWizardPage;

function PowerShellPath: string;
begin
  Result := ExpandConstant('{autopf}\PowerShell\7\pwsh.exe');
end;

function GetDataRoot(Param: string): string;
begin
  if DataPage <> nil then
    Result := DataPage.Values[0]
  else if not RegQueryStringValue(HKLM, ProductRegistryKey, 'DataRoot', Result) then
    Result := '';
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

function GetSchoolSubnet: string;
begin
  Result := NetworkPage.Values[2];
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
  if AllowUnsignedDevelopmentBuild = 1 then
    Result := 'Bypass'
  else
    Result := 'AllSigned';
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
    ' -OfflineConfirmed -InstallerManagedApplicationRemoval -Confirm:$false';
end;

function BuildInstallParameters: string;
begin
  Result := GetCommonPowerShellParameters() +
    ' -File ' + AddQuotes(ExpandConstant('{tmp}\OokiGraderPackage\Install-OokiGrader.ps1')) +
    ' -PackageRoot ' + AddQuotes(ExpandConstant('{tmp}\OokiGraderPackage')) +
    ' -Version ' + AddQuotes('{#OokiVersion}') +
    ' -DataRoot ' + AddQuotes(GetDataRoot('')) +
    ' -HostCertificatePath ' + AddQuotes(CertificatePage.Values[0]) +
    ' -DnsName ' + AddQuotes(GetDnsName('')) +
    ' -SchoolSubnet ' + AddQuotes(GetSchoolSubnet()) +
    ' -InstallRoot ' + AddQuotes(ExpandConstant('{app}')) +
    ' -HttpsPort ' + GetHttpsPort('') +
    ' -ExpectedSignerThumbprint ' + AddQuotes(ExpectedSignerThumbprint) +
    ' -Confirm:$false';
  if AllowUnsignedDevelopmentBuild = 1 then
    Result := Result + ' -AllowUnsignedDevelopmentBuild';
end;

function InitializeSetup: Boolean;
var
  ExistingVersion: string;
  ExitCode: Integer;
  VersionCheck: string;
begin
  Result := True;
  if not FileExists(PowerShellPath()) then
  begin
    MsgBox(
      'PowerShell 7.4 以降 (64-bit) が必要です。Microsoft の PowerShell 7 をインストールしてから、もう一度実行してください。',
      mbError, MB_OK);
    Result := False;
    Exit;
  end;
  VersionCheck := '-NoLogo -NoProfile -NonInteractive -Command ' +
    AddQuotes('if ($PSVersionTable.PSVersion -lt [version]''7.4'') { exit 4 }');
  if (not Exec(
      PowerShellPath(),
      VersionCheck,
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ExitCode)) or (ExitCode <> 0) then
  begin
    MsgBox(
      '検出した PowerShell が 7.4 未満です。PowerShell 7.4 以降 (64-bit) に更新してください。',
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
  DefaultDataRoot: string;
begin
  DataPage := CreateInputDirPage(
    wpSelectDir,
    'データ保存先',
    '生徒情報・答案・AI認証情報を保存する専用フォルダーを指定してください。',
    'アプリ本体とは別のローカル NTFS ドライブを推奨します。アンインストールしてもこのデータは削除されません。',
    False,
    'OokiGraderData');
  if RegQueryStringValue(HKLM, ProductRegistryKey, 'DataRoot', ExistingValue) then
    DefaultDataRoot := ExistingValue
  else if DirExists('D:\') then
    DefaultDataRoot := 'D:\OokiGraderData'
  else
    DefaultDataRoot := ExpandConstant('{commonappdata}\OokiGrader\data');
  DataPage.Add(DefaultDataRoot);

  NetworkPage := CreateInputQueryPage(
    DataPage.ID,
    '校内ネットワーク',
    'Ooki Grader の校内 URL と接続範囲を指定してください。',
    'DNS 名と HTTPS ポートは証明書・ブラウザー URL と一致する必要があります。');
  NetworkPage.Add('DNS 名:', False);
  NetworkPage.Add('HTTPS ポート:', False);
  NetworkPage.Add('許可する校内サブネット (CIDR):', False);
  if not RegQueryStringValue(HKLM, ProductRegistryKey, 'DnsName', ExistingValue) then
    ExistingValue := 'ooki-grader.local';
  NetworkPage.Values[0] := ExistingValue;
  if not RegQueryStringValue(HKLM, ProductRegistryKey, 'HttpsPort', ExistingValue) then
    ExistingValue := '443';
  NetworkPage.Values[1] := ExistingValue;
  NetworkPage.Values[2] := '192.168.0.0/16';

  CertificatePage := CreateInputFilePage(
    NetworkPage.ID,
    'HTTPS 証明書',
    '指定した DNS 名の秘密鍵付き証明書を選択してください。',
    'New-OokiGraderCertificate.ps1 で作成した空パスワードの PFX/P12 を選択します。');
  CertificatePage.Add('証明書:', 'PKCS#12 (*.pfx;*.p12)|*.pfx;*.p12', '.pfx');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Port: Integer;
  Extension: string;
begin
  Result := True;
  if CurPageID = DataPage.ID then
  begin
    if (Trim(GetDataRoot('')) = '') or
       (ExtractFileDrive(GetDataRoot('')) = '') or
       (Pos('\\', GetDataRoot('')) = 1) then
    begin
      MsgBox('データ保存先にはローカルドライブ上の絶対パスを指定してください。', mbError, MB_OK);
      Result := False;
    end;
  end
  else if CurPageID = NetworkPage.ID then
  begin
    Port := StrToIntDef(GetHttpsPort(''), 0);
    if (Trim(GetDnsName('')) = '') or
       (Pos(' ', GetDnsName('')) > 0) or
       (Port < 1) or (Port > 65535) or
       (Trim(GetSchoolSubnet()) = '') then
    begin
      MsgBox('DNS 名、1〜65535 の HTTPS ポート、明示的な校内 CIDR を確認してください。', mbError, MB_OK);
      Result := False;
    end;
  end
  else if CurPageID = CertificatePage.ID then
  begin
    Extension := Lowercase(ExtractFileExt(CertificatePage.Values[0]));
    if (not FileExists(CertificatePage.Values[0])) or
       ((Extension <> '.pfx') and (Extension <> '.p12')) then
    begin
      MsgBox('有効な PFX/P12 証明書ファイルを選択してください。', mbError, MB_OK);
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
