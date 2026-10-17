; DeskCanvas Inno Setup Script
; Produces DeskCanvas-Setup.exe with native uninstallation (unins000.exe) and dynamic data directory cleanup support.

#ifndef MyAppVersion
#define MyAppVersion "3.8.3"
#endif

#ifndef MyRuntime
#define MyRuntime "win-x64"
#endif

#ifndef MyArch
#define MyArch "x64"
#endif

[Setup]
AppId={{9E38A34F-7561-4B9F-8463-7186F0BA0930}
AppName=DeskCanvas
AppVersion={#MyAppVersion}
AppPublisher=Genlue
AppPublisherURL=https://github.com/Genlue/DeskCanvas
AppSupportURL=https://github.com/Genlue/DeskCanvas/issues
AppUpdatesURL=https://github.com/Genlue/DeskCanvas/releases
DefaultDirName={autopf}\DeskCanvas
DefaultGroupName=DeskCanvas
AllowNoIcons=yes
LicenseFile=License.rtf
OutputDir=..\dist\installer
OutputBaseFilename=DeskCanvas-{#MyAppVersion}-{#MyArch}-Setup
SetupIconFile=..\.github\images\icon.ico
UninstallDisplayIcon={app}\DeskCanvas.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=DeskCanvas.exe
#if MyArch == "x64"
ArchitecturesInstallIn64BitMode=x64compatible
#endif
Uninstallable=yes
UsedUserAreasWarning=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\dist\{#MyRuntime}\DeskCanvas.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "License.rtf"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\DeskCanvas"; Filename: "{app}\DeskCanvas.exe"
Name: "{group}\卸载 DeskCanvas"; Filename: "{uninstallexe}"
Name: "{autodesktop}\DeskCanvas"; Filename: "{app}\DeskCanvas.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\DeskCanvas.exe"; Description: "{cm:LaunchProgram,DeskCanvas}"; Flags: nowait postinstall skipifsilent

; No [Registry] section on purpose. The previous entries deleted the shared
; HKCU\Software\Genlue\DeskCanvas key on uninstall (that key belongs to the MSI
; installer / app settings and was never created here), and deleted the shared
; Run value even when it pointed at a different installation. Autostart cleanup
; is now scoped to this installation in [Code].

[Code]
var
  RemoveUserData: Boolean;

// Simple JSON parser helper to extract the "DataFolder" property value from dataFolder.json
function ExtractDataFolderFromJson(const FilePath: string): string;
var
  Lines: TArrayOfString;
  I, KeyPos, ValStart, ValEnd: Integer;
  Line, Content: string;
begin
  Result := '';
  if not FileExists(FilePath) then Exit;

  if LoadStringsFromFile(FilePath, Lines) then
  begin
    Content := '';
    for I := 0 to GetArrayLength(Lines) - 1 do
      Content := Content + Lines[I] + ' ';

    KeyPos := Pos('"DataFolder"', Content);
    if KeyPos > 0 then
    begin
      // Skip past key and colon
      ValStart := KeyPos + Length('"DataFolder"');
      while (ValStart <= Length(Content)) and (Content[ValStart] <> ':') do
        Inc(ValStart);
      Inc(ValStart); // Skip ':'

      // Find opening quote of value
      while (ValStart <= Length(Content)) and (Content[ValStart] <> '"') do
        Inc(ValStart);
      Inc(ValStart); // Skip opening quote

      ValEnd := ValStart;
      while (ValEnd <= Length(Content)) and (Content[ValEnd] <> '"') do
      begin
        if (Content[ValEnd] = '\') and (ValEnd < Length(Content)) then
          ValEnd := ValEnd + 2
        else
          Inc(ValEnd);
      end;

      if ValEnd > ValStart then
      begin
        Line := Copy(Content, ValStart, ValEnd - ValStart);
        StringChange(Line, '\\', '\');
        Result := Trim(Line);
      end;
    end;
  end;
end;

// Dynamically resolves the data folder, strictly matching Const.cs resolution order
function ResolveDataDirectory(): string;
var
  EnvDir, ExePointer, AnchorPointer: string;
begin
  // 1. Environment variable override
  EnvDir := GetEnv('DESKCANVAS_DATA_FOLDER');
  if EnvDir = '' then
    EnvDir := GetEnv('DESCANVAS_DATA_FOLDER');
  if EnvDir <> '' then
  begin
    Result := Trim(EnvDir);
    Exit;
  end;

  // 2. Exe pointer next to DeskCanvas.exe in app directory
  ExePointer := ExtractDataFolderFromJson(ExpandConstant('{app}\dataFolder.json'));
  if ExePointer <> '' then
  begin
    Result := ExePointer;
    Exit;
  end;

  // 3. Anchor pointer in default local app data
  AnchorPointer := ExtractDataFolderFromJson(ExpandConstant('{localappdata}\DeskCanvas\dataFolder.json'));
  if AnchorPointer <> '' then
  begin
    Result := AnchorPointer;
    Exit;
  end;

  // 4. Default packaged folder
  Result := ExpandConstant('{localappdata}\DeskCanvas');
end;

function CmdLineParamExists(const ParamName: string): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
  begin
    if CompareText(ParamStr(I), ParamName) = 0 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function InitializeUninstall(): Boolean;
var
  PromptMsg: string;
begin
  Result := True;
  RemoveUserData := False;

  if CmdLineParamExists('/REMOVEDATA') or CmdLineParamExists('--remove-data') then
  begin
    RemoveUserData := True;
    Exit;
  end;

  // In non-silent mode, ask user whether to delete user data.
  // Default is NO (MB_DEFBUTTON2) per user directive!
  if not UninstallSilent then
  begin
    PromptMsg := '是否同时删除 DeskCanvas 的用户数据（包含所有配置方案、组件布局与小组件数据）？' + #13#10 + #13#10 +
                 '· 默认推荐：点击【否】以保留您的配置，方便日后重新安装使用。' + #13#10 +
                 '· 彻底清除：点击【是】将永久删除全部本地数据。';

    if MsgBox(PromptMsg, mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      RemoveUserData := True;
  end;
end;

// Deletes the autostart value only when it points at this installation, so uninstalling
// one copy can never disable another copy's autostart.
procedure RemoveOwnAutoStart();
var
  RunValue, AppPath: string;
begin
  if RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run', 'DeskCanvas', RunValue) then
  begin
    AppPath := ExpandConstant('{app}');
    if Pos(Lowercase(AppPath), Lowercase(RunValue)) > 0 then
      RegDeleteValue(HKCU, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run', 'DeskCanvas');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  TargetDataDir, DefaultAnchorDir: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    // Remove the autostart entry only when it still points into this installation.
    // A plain uninsdeletevalue entry deletes the shared "DeskCanvas" value even when it
    // belongs to another installation (e.g. an MSI install of the same product), silently
    // disabling that copy's autostart. {app} is still resolvable at usUninstall.
    RemoveOwnAutoStart();
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    if RemoveUserData then
    begin
      TargetDataDir := ResolveDataDirectory();
      if (TargetDataDir <> '') and DirExists(TargetDataDir) then
      begin
        DelTree(TargetDataDir, True, True, True);
      end;

      DefaultAnchorDir := ExpandConstant('{localappdata}\DeskCanvas');
      if DirExists(DefaultAnchorDir) and (CompareText(TargetDataDir, DefaultAnchorDir) <> 0) then
      begin
        DelTree(DefaultAnchorDir, True, True, True);
      end;
    end;
  end;
end;
