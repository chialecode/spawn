; SPAWN per-user installer template (Inno Setup 7).
; Invoked by `python scripts/package.py`, which supplies channel/identity defines via /D.
; Strict invariants:
;   1. PrivilegesRequired=lowest -> installs per-user under %LOCALAPPDATA%\Programs\<Identity> (zero UAC prompt).
;   2. Isolated AppId, mutex, shortcuts, and directory per identity (spawn / spawn-dev / spawn-dev-test).
;   3. Zero [UninstallDelete] on %LOCALAPPDATA%\spawn* data directories -> uninstall never deletes user saves or DB.
;   4. Never force-kills running instances -> requests graceful `--quit-for-update` and aborts if busy after timeout.

#ifndef Identity
  #error Identity must be defined via /DIdentity=...
#endif
#ifndef AppGuid
  #error AppGuid must be defined via /DAppGuid=...
#endif
#ifndef DisplayName
  #error DisplayName must be defined via /DDisplayName=...
#endif
#ifndef Version
  #error Version must be defined via /DVersion=...
#endif
#ifndef NumericVersion
  #error NumericVersion must be defined via /DNumericVersion=...
#endif
#ifndef DesktopExe
  #error DesktopExe must be defined via /DDesktopExe=...
#endif
#ifndef IconFile
  #error IconFile must be defined via /DIconFile=...
#endif
#ifndef SourceDir
  #error SourceDir must be defined via /DSourceDir=...
#endif
#ifndef OutputDir
  #error OutputDir must be defined via /DOutputDir=...
#endif
#ifndef OutputBase
  #error OutputBase must be defined via /DOutputBase=...
#endif
#ifndef Aumid
  #define Aumid "ChialeCode.Spawn"
#endif
#ifndef CompressionMode
  #define CompressionMode "lzma2/fast"
#endif
#ifndef CreateShortcuts
  #define CreateShortcuts "1"
#endif
#ifndef DesktopOnUpdate
  #define DesktopOnUpdate "1"
#endif

#define AppIdValue "{{" + AppGuid + "}"
#define MutexList Identity + "-instance,Local\" + Identity + "-instance,Global\" + Identity + "-instance"

[Setup]
AppId={#AppIdValue}
AppName={#DisplayName}
AppVersion={#Version}
AppVerName={#DisplayName} {#Version}
AppPublisher=SPAWN Contributors
AppCopyright=Copyright (C) SPAWN Contributors. Licensed under Apache-2.0.
VersionInfoVersion={#NumericVersion}
VersionInfoProductName={#DisplayName}
VersionInfoProductTextVersion={#Version}
VersionInfoDescription={#DisplayName} Per-User Installer
DefaultDirName={localappdata}\Programs\{#Identity}
UsePreviousAppDir=no
DisableDirPage=yes
DisableProgramGroupPage=yes
DefaultGroupName={#DisplayName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex={#MutexList}
SetupMutex={#Identity}-setup
CloseApplications=no
RestartApplications=no
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\{#DesktopExe}
UninstallDisplayName={#DisplayName}
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBase}
Compression={#CompressionMode}
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no
SetupLogging=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.CreateStartMenuIcon=创建开始菜单快捷方式(&S)
english.CreateStartMenuIcon=Create a &Start Menu shortcut

[Tasks]
Name: "startmenuicon"; Description: "{cm:CreateStartMenuIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Check: AllowShortcuts
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Check: AllowShortcuts

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#DisplayName}"; Filename: "{app}\{#DesktopExe}"; IconFilename: "{app}\{#DesktopExe}"; AppUserModelID: "{#Aumid}"; Check: WantStartMenuIcon
Name: "{autodesktop}\{#DisplayName}"; Filename: "{app}\{#DesktopExe}"; IconFilename: "{app}\{#DesktopExe}"; AppUserModelID: "{#Aumid}"; Check: WantDesktopIcon

[Run]
Filename: "{app}\{#DesktopExe}"; Description: "{cm:LaunchProgram,{#DisplayName}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  QuitWaitMs = 30000;
  UninstallKeyPrefix = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\';
#if CreateShortcuts == "1"
  ShortcutsEnabled = True;
#else
  ShortcutsEnabled = False;
#endif
#if DesktopOnUpdate == "1"
  RecreateDesktopIconOnUpdate = True;
#else
  RecreateDesktopIconOnUpdate = False;
#endif

var
  FirstInstall: Boolean;
  TaskDefaultsApplied: Boolean;

function AllowShortcuts: Boolean;
begin
  Result := ShortcutsEnabled;
end;

function StartMenuIconPath: String;
begin
  Result := ExpandConstant('{autoprograms}\{#DisplayName}.lnk');
end;

function DesktopIconPath: String;
begin
  Result := ExpandConstant('{autodesktop}\{#DisplayName}.lnk');
end;

function WantStartMenuIcon: Boolean;
begin
  if not ShortcutsEnabled then
    Result := False
  else if WizardSilent then
    Result := FirstInstall or RecreateDesktopIconOnUpdate or FileExists(StartMenuIconPath)
  else
    Result := WizardIsTaskSelected('startmenuicon');
end;

function WantDesktopIcon: Boolean;
begin
  if not ShortcutsEnabled then
    Result := False
  else if WizardSilent then
    Result := FirstInstall or RecreateDesktopIconOnUpdate or FileExists(DesktopIconPath)
  else
    Result := WizardIsTaskSelected('desktopicon');
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID <> wpSelectTasks) or FirstInstall or TaskDefaultsApplied or (not ShortcutsEnabled) then
    exit;
  TaskDefaultsApplied := True;
  if FileExists(StartMenuIconPath) then
    WizardSelectTasks('startmenuicon')
  else
    WizardSelectTasks('!startmenuicon');
  if FileExists(DesktopIconPath) then
    WizardSelectTasks('desktopicon')
  else
    WizardSelectTasks('!desktopicon');
end;

procedure RetireLockedBinariesInDir(DirPath, RetireRoot: String);
var
  FindRec: TFindRec;
  FullPath, SubRetire: String;
  ProbeStream: TFileStream;
begin
  if not DirExists(DirPath) then
    exit;
  if FindFirst(AddBackslash(DirPath) + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name = '.') or (FindRec.Name = '..') or (FindRec.Name = '.retired') then
          continue;
        FullPath := AddBackslash(DirPath) + FindRec.Name;
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          SubRetire := AddBackslash(RetireRoot) + FindRec.Name;
          RetireLockedBinariesInDir(FullPath, SubRetire);
        end
        else
        begin
          if (CompareText(FindRec.Name, 'unins000.exe') = 0) or (CompareText(FindRec.Name, 'unins000.dat') = 0) then
            continue;
          try
            ProbeStream := TFileStream.Create(FullPath, fmOpenReadWrite or fmShareDenyNone);
            ProbeStream.Free;
          except
            ForceDirectories(RetireRoot);
            RenameFile(FullPath, AddBackslash(RetireRoot) + FindRec.Name + '.' + GetDateTimeString('yyyymmddhhnnss', #0, #0));
            Log('SPAWN Setup: retired kernel-cached binary ' + FullPath);
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  AppDir, RetireBase: String;
begin
  if CurStep = ssInstall then
  begin
    AppDir := ExpandConstant('{app}');
    if DirExists(AppDir) then
    begin
      RetireBase := ExpandConstant('{tmp}\retired');
      DelTree(AddBackslash(AppDir) + '.retired', True, True, True);
      RetireLockedBinariesInDir(AppDir, RetireBase);
    end;
    exit;
  end;
  if (CurStep <> ssPostInstall) or WizardSilent or (not ShortcutsEnabled) then
    exit;
  if not WizardIsTaskSelected('startmenuicon') and FileExists(StartMenuIconPath) then
    DeleteFile(StartMenuIconPath);
  if not WizardIsTaskSelected('desktopicon') and FileExists(DesktopIconPath) then
    DeleteFile(DesktopIconPath);
end;

function WaitForAppExit(TimeoutMs: Integer): Boolean;
var
  Waited: Integer;
begin
  Waited := 0;
  while CheckForMutexes('{#MutexList}') and (Waited < TimeoutMs) do
  begin
    Sleep(200);
    Waited := Waited + 200;
  end;
  Result := not CheckForMutexes('{#MutexList}');
end;

function ResolveInstalledExePath: String;
var
  InstallLoc: String;
begin
  InstallLoc := '';
  if RegQueryStringValue(HKCU, UninstallKeyPrefix + '{' + '{#AppGuid}' + '}_is1', 'InstallLocation', InstallLoc) and (InstallLoc <> '') then
    Result := AddBackslash(InstallLoc) + '{#DesktopExe}'
  else
    Result := ExpandConstant('{localappdata}\Programs\{#Identity}\{#DesktopExe}');
end;

function RequestGracefulQuit: Boolean;
var
  ExePath: String;
  ResultCode: Integer;
begin
  Result := True;
  if not CheckForMutexes('{#MutexList}') then
    exit;
  ExePath := ResolveInstalledExePath;
  Log('SPAWN Setup: {#Identity} mutex active; requesting --quit-for-update via ' + ExePath);
  if FileExists(ExePath) then
  begin
    if Exec(ExePath, '--quit-for-update', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      Log(Format('SPAWN Setup: --quit-for-update returned %d', [ResultCode]))
    else
      Log('SPAWN Setup: failed to start --quit-for-update helper');
  end;
  if WaitForAppExit(QuitWaitMs) then
  begin
    Log('SPAWN Setup: running instance exited cleanly');
    exit;
  end;
  Log('SPAWN Setup: instance still running after timeout; aborting without force-kill');
  Result := False;
end;

function InitializeSetup(): Boolean;
begin
  FirstInstall := not RegValueExists(HKCU,
    UninstallKeyPrefix + '{' + '{#AppGuid}' + '}_is1', 'UninstallString');
  Result := RequestGracefulQuit;
  if (not Result) and (not WizardSilent) then
    MsgBox('{#DisplayName} is still running or busy. Please close it and run Setup again.',
      mbError, MB_OK);
end;

function InitializeUninstall(): Boolean;
begin
  Result := RequestGracefulQuit;
  if (not Result) and (not UninstallSilent) then
    MsgBox('{#DisplayName} is still running or busy. Please close it before uninstalling.',
      mbError, MB_OK);
end;
