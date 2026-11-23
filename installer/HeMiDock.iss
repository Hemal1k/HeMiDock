; HeMiDock installer (Inno Setup 6). Built by release.ps1:  ISCC /DAppVersion=1.5.0 /DSourceDir=<published app> /DOutputDir=<folder>
; Installs for the current user (no administrator) or for all users (asked), checks the .NET 10 Runtime and the Windows App Runtime 2.6 and installs
; what is missing from Microsoft (only that asks for administrator rights). /UPDATE (the update of the app): quiet, starts the
; dock again afterwards.

; the Windows App Runtime the app needs at least: release.ps1 passes the version of the Windows App SDK of the app
; (HeMiDock built with 2.6.4 does not start on 2.6.0 – 2.6.3: the check of 2.6.0 let the installer skip it, 2026-11-19)
#ifndef AppRuntimeMin
  #define AppRuntimeMin "2.6.4.0"
#endif
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\build\publish"
#endif
#ifndef SourceDirArm
  #define SourceDirArm ""
#endif
#ifndef OutputDir
  #define OutputDir "..\build\installer"
#endif

[Setup]
AppId=HeMiDock
AppName=HeMiDock
AppVersion={#AppVersion}
AppVerName=HeMiDock {#AppVersion}
AppPublisher=Richard Hemala
AppPublisherURL=https://github.com/Hemal1k/HeMiDock
AppSupportURL=https://github.com/Hemal1k/HeMiDock/issues
AppUpdatesURL=https://github.com/Hemal1k/HeMiDock/releases
DefaultDirName={autopf}\HeMi\HeMiDock
UsePreviousAppDir=no
DisableDirPage=yes
DisableProgramGroupPage=yes
DefaultGroupName=HeMiDock
PrivilegesRequired=lowest
; the first page asks: for me only (default, no administrator) or for all users (Program Files, asks for administrator)
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible or arm64
ArchitecturesInstallIn64BitMode=x64compatible or arm64
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=HeMiDock-Setup
SetupIconFile=..\src\Assets\HeMiDock.ico
UninstallDisplayIcon={app}\HeMiDock.exe
UninstallDisplayName=HeMiDock
Compression=lzma2/max
SolidCompression=yes
; the look of Windows 11, light or dark as Windows, and as big as the first question of the installer (it was
; smaller and old-looking next to it, 2026-11-19)
WizardStyle=modern dynamic windows11
WizardSizePercent=120
CloseApplications=force
RestartApplications=no
LicenseFile=..\LICENSE

[Languages]
; English only
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
en.Prereq=Installing what HeMiDock needs: %1
en.PrereqFailed=%1 could not be installed. Please install it by hand and run the installer again:%n%2
en.LaunchAfter=Start HeMiDock
en.Updates=Updates:
en.UpdateCheck=Look for new versions automatically
en.UpdateInstall=Install them automatically (the dock restarts)
en.NeedTitle=Components from Microsoft
en.NeedDesc=HeMiDock needs components that are missing on this PC.
en.NeedText=The installer downloads them directly from Microsoft and installs them (Windows asks for administrator rights). They come with Microsoft's license terms – please read them before you continue:
en.NeedLicense=license terms
en.NeedAccept=I accept the license terms of these components and agree to download and install them

[Files]
; x64, and the ARM64 build on Windows on ARM when the installer has it
#if SourceDirArm != ""
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: not IsArm64
Source: "{#SourceDirArm}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsArm64
#else
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif

[Tasks]
; offered on its own page, off by default
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
; the updates of the dock (written for it to take at its next start – see SaveUpdateChoice); installing them needs looking for them.
; Not asked when HeMi Launcher installs it (/LAUNCHER): the launcher looks for and installs the updates itself
Name: "updates"; Description: "{cm:UpdateCheck}"; GroupDescription: "{cm:Updates}"; Check: NotFromLauncher
; two separate boxes (as parent and child, unticking the second unticked the first too, 2026-11-19)
Name: "updatesinstall"; Description: "{cm:UpdateInstall}"; GroupDescription: "{cm:Updates}"; Flags: unchecked; Check: NotFromLauncher

[Icons]
Name: "{autoprograms}\HeMiDock"; Filename: "{app}\HeMiDock.exe"
Name: "{autodesktop}\HeMiDock"; Filename: "{app}\HeMiDock.exe"; Tasks: desktopicon

[Run]
; the File Explorer items and the start with Windows point to this installation (also after a move from the old folder) –
; only over an earlier installation: a new one has nothing to point anew
; not waited for: on a PC without the Windows App Runtime (a quiet install that could not add it) the app would wait for an
; answer to the question of Windows about it, and Setup would never end
Filename: "{app}\HeMiDock.exe"; Parameters: "--repair"; Flags: runasoriginaluser nowait runhidden; Check: WasInstalled
; after an interactive installation: offered; after an update (quiet): the dock starts again by itself
; (as the user who started the installer – never as administrator after an installation for all users)
Filename: "{app}\HeMiDock.exe"; Description: "{cm:LaunchAfter}"; Flags: nowait postinstall skipifsilent runasoriginaluser
Filename: "{app}\HeMiDock.exe"; Parameters: "--after-update"; Flags: nowait runasoriginaluser; Check: DockWasRunning

[UninstallRun]
Filename: "{app}\HeMiDock.exe"; Parameters: "--uninstall"; Flags: waituntilterminated; RunOnceId: "HeMiDockUninstall"
; the same once more without the app (when it could not run): the task of the start at sign-in
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""HeMiDock ({username})"" /F"; Flags: runhidden; RunOnceId: "HeMiDockTask"

[Registry]
; the start at sign-in (also the name of the test builds) and what Windows itself keeps about the app (its name in
; menus, "Open with", Task Manager → Startup apps) – removed with it
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "HeMiDock"; Flags: uninsdeletevalue dontcreatekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "HeMiDock.WinUI"; Flags: uninsdeletevalue dontcreatekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"; ValueType: none; ValueName: "HeMiDock"; Flags: uninsdeletevalue dontcreatekey
Root: HKCU; Subkey: "Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache"; ValueType: none; ValueName: "{app}\HeMiDock.exe.FriendlyAppName"; Flags: uninsdeletevalue dontcreatekey
Root: HKCU; Subkey: "Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache"; ValueType: none; ValueName: "{app}\HeMiDock.exe.ApplicationCompany"; Flags: uninsdeletevalue dontcreatekey
Root: HKCU; Subkey: "Software\Classes\Applications\HeMiDock.exe"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\HeMi\HeMiDock"; ValueType: none; ValueName: "SetupCheckUpdates"; Flags: uninsdeletevalue dontcreatekey
Root: HKCU; Subkey: "Software\HeMi\HeMiDock"; ValueType: none; ValueName: "SetupInstallUpdates"; Flags: uninsdeletevalue dontcreatekey
Root: HKCU; Subkey: "Software\HeMi\HeMiDock"; Flags: uninsdeletekeyifempty dontcreatekey
Root: HKCU; Subkey: "Software\HeMi"; Flags: uninsdeletekeyifempty dontcreatekey

[Code]
// Since 2026-11 the HeMi apps go into one folder HeMi, as their settings do (%AppData%\HeMi\<app>): {autopf}\HeMi\HeMiDock.
// An installation in the old folder {autopf}\HeMiDock moves: the app running from there ends, the new version goes into the
// new folder ("--repair" points the File Explorer items and the start with Windows to it) and the old folder is deleted.
var
  OldFolder: String;
  // the dock ran when the installation began (an update): only then it starts again after a quiet installation – a new
  // quiet installation does not start it; /UPDATE (the updater of the dock, HeMi Launcher) always does: the dock
  // ends before the installer gets to look
  WasRunning: Boolean;
  // HeMiDock was installed before (an update, a repair or a move from the old folder)
  Upgrade: Boolean;

{ installed by HeMi Launcher (/LAUNCHER): it takes care of the updates, the installer does not ask about them }
function NotFromLauncher: Boolean;
begin
  Result := Pos('/LAUNCHER', Uppercase(GetCmdTail)) = 0;
end;

function WasInstalled: Boolean;
begin
  Result := Upgrade;
end;

function DockWasRunning: Boolean;
begin
  Result := WizardSilent and (WasRunning or (Pos('/UPDATE', Uppercase(GetCmdTail)) > 0));
end;

function FindOldFolder: String;
begin
  if RegQueryStringValue(HKA, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\HeMiDock_is1', 'InstallLocation', Result) then
  begin
    Result := RemoveBackslashUnlessRoot(Result);
    if (CompareText(Result, ExpandConstant('{app}')) = 0) or (CompareText(ExtractFileName(Result), 'HeMiDock') <> 0)
      or not FileExists(Result + '\HeMiDock.exe') then
      Result := '';
  end
  else
    Result := '';
end;

procedure StopOld;
var
  Code: Integer;
begin
  OldFolder := FindOldFolder;
  if OldFolder = '' then Exit;
  // the dock gives the taskbar of Windows back first
  Exec(OldFolder + '\HeMiDock.exe', '--exit', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(1500);
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "Get-Process | Where-Object { $_.Path -like ''' + OldFolder + '\*'' } | Stop-Process -Force"',
    '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure DeleteOld;
begin
  if OldFolder <> '' then DelTree(OldFolder, True, True, True);
end;

{ the choice of the updates for the dock to take at its next start (HKCU\Software\HeMi\HeMiDock) – not by starting it. A
  quiet update keeps what was set in the app; a new installation (also a quiet one) and an installation by hand write it. }
procedure SaveUpdateChoice;
var
  Check, Install: Cardinal;
begin
  if WizardSilent and Upgrade then Exit;
  Check := 0; Install := 0;
  // HeMi Launcher keeps the dock up to date: the dock itself does not look (it can be switched on in its settings)
  if NotFromLauncher and WizardIsTaskSelected('updates') then Check := 1;
  { installing needs looking: only with "Look for new versions" }
  if NotFromLauncher and WizardIsTaskSelected('updates') and WizardIsTaskSelected('updatesinstall') then Install := 1;
  RegWriteDWordValue(HKCU, 'Software\HeMi\HeMiDock', 'SetupCheckUpdates', Check);
  RegWriteDWordValue(HKCU, 'Software\HeMi\HeMiDock', 'SetupInstallUpdates', Install);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then SaveUpdateChoice;   { before the dock may start again at the end }
  if CurStep = ssPostInstall then DeleteOld;
end;

const
  DotNetLicense = 'https://aka.ms/dotnet-license';
  AppRuntimeLicense = 'https://www.nuget.org/packages/Microsoft.WindowsAppSDK.Runtime/2.6.5/License';

var
  DownloadPage: TDownloadWizardPage;
  NeedPage: TWizardPage;
  AcceptBox: TNewCheckBox;
  NeedDotNet, NeedRuntime: Boolean;

{ the processor the app is installed for: arm64 on Windows on ARM when the installer has that build, else x64 }
function Arch: String;
begin
#if SourceDirArm != ""
  if IsArm64 then Result := 'arm64' else Result := 'x64';
#else
  Result := 'x64';
#endif
end;

function DotNetUrl: String;
begin
  Result := 'https://aka.ms/dotnet/10.0/dotnet-runtime-win-' + Arch + '.exe';
end;

function AppRuntimeUrl: String;
begin
  Result := 'https://aka.ms/windowsappsdk/2.6/latest/windowsappruntimeinstall-' + Arch + '.exe';
end;

{ the .NET 10 Runtime of that processor: a folder 10.x of Microsoft.NETCore.App }
function HasDotNet: Boolean;
var
  Found: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.NETCore.App\10.*'), Found);
  if Result then FindClose(Found);
end;

{ the Windows App Runtime the app was built with, or newer, of this processor – asked by PowerShell }
function AppxArch: String;
begin
  if Arch = 'arm64' then Result := 'Arm64' else Result := 'X64';
end;

function HasAppRuntime: Boolean;
var
  Code: Integer;
begin
  Result := Exec('powershell.exe',
    '-NoProfile -ExecutionPolicy Bypass -Command "if (Get-AppxPackage -Name Microsoft.WindowsAppRuntime.2 | Where-Object { $_.Architecture -eq ''' + AppxArch + ''' -and $_.Version -ge [version]''{#AppRuntimeMin}'' }) { exit 0 } else { exit 1 }"',
    '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

{ the running dock ends before its files are replaced (it gives the taskbar of Windows back) }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
  WasRunning := CheckForMutexes('HeMiDock_SingleInstance');
  Upgrade := RegKeyExists(HKLM, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\HeMiDock_is1')
    or RegKeyExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\HeMiDock_is1') or (OldFolder <> '');
  StopOld;
  if FileExists(ExpandConstant('{app}\HeMiDock.exe')) then
  begin
    Exec(ExpandConstant('{app}\HeMiDock.exe'), '--exit', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Sleep(1500);
  end;
end;

{ .NET goes into Program Files and needs administrator rights: asked for (UAC) only when Setup does not have them }
function RunElevated(const FileName, Params: String): Boolean;
var
  Code: Integer;
begin
  if IsAdmin then
    Result := Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) and ((Code = 0) or (Code = 3010))
  else
    Result := ShellExec('runas', FileName, Params, '', SW_SHOW, ewWaitUntilTerminated, Code) and ((Code = 0) or (Code = 3010));
end;

{ the Windows App Runtime needs no administrator rights: without them it is installed for this user only (no UAC prompt –
  a quiet install has nobody to answer one) }
function RunPlain(const FileName, Params: String): Boolean;
var
  Code: Integer;
begin
  Result := Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) and ((Code = 0) or (Code = 3010));
end;

procedure OpenLink(Sender: TObject);
var
  Code: Integer;
begin
  ShellExec('open', TNewStaticText(Sender).Hint, '', '', SW_SHOWNORMAL, ewNoWait, Code);
end;

procedure AcceptClick(Sender: TObject);
begin
  WizardForm.NextButton.Enabled := AcceptBox.Checked;
end;

{ one missing component: its name and a link to its license terms }
function AddComponent(const Name, License: String; Top: Integer): Integer;
var
  N, L: TNewStaticText;
begin
  N := TNewStaticText.Create(NeedPage);
  N.Parent := NeedPage.Surface;
  N.Top := Top;
  N.Left := ScaleX(8);
  N.Caption := '•  ' + Name + '  –  ';
  N.Font.Style := [fsBold];
  L := TNewStaticText.Create(NeedPage);
  L.Parent := NeedPage.Surface;
  L.Top := Top;
  L.Left := N.Left + N.Width;
  L.Caption := CustomMessage('NeedLicense');
  L.Hint := License;
  L.Cursor := crHand;
  L.Font.Color := clHotLight;
  L.Font.Style := [fsUnderline];
  L.OnClick := @OpenLink;
  Result := Top + L.Height + ScaleY(8);
end;

procedure InitializeWizard;
var
  T: TNewStaticText;
  Y: Integer;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
  NeedDotNet := not HasDotNet;
  NeedRuntime := not HasAppRuntime;
  { a quiet installation without administrator rights never installs .NET: its installer needs them and would wait for
    a UAC prompt nobody answers; Windows asks for it when HeMiDock starts }
  if WizardSilent and not IsAdmin then NeedDotNet := False;
#ifdef TestNeedPage
  NeedDotNet := True; NeedRuntime := True;   { ISCC /DTestNeedPage: shows the page on a PC that has everything }
#endif

  { what is missing is shown before anything is downloaded; Next only after the license terms are accepted }
  NeedPage := CreateCustomPage(wpSelectTasks, CustomMessage('NeedTitle'), CustomMessage('NeedDesc'));
  T := TNewStaticText.Create(NeedPage);
  T.Parent := NeedPage.Surface;
  T.Width := NeedPage.SurfaceWidth;
  T.WordWrap := True;
  T.AutoSize := True;
  T.Caption := CustomMessage('NeedText');
  Y := T.Top + T.Height + ScaleY(16);
  if NeedDotNet then Y := AddComponent('.NET 10 Runtime (' + Arch + ')', DotNetLicense, Y);
  if NeedRuntime then Y := AddComponent('Windows App Runtime 2.6 (' + Arch + ')', AppRuntimeLicense, Y);
  AcceptBox := TNewCheckBox.Create(NeedPage);
  AcceptBox.Parent := NeedPage.Surface;
  AcceptBox.Top := Y + ScaleY(12);
  AcceptBox.Width := NeedPage.SurfaceWidth;
  AcceptBox.Height := ScaleY(34);
  AcceptBox.Caption := CustomMessage('NeedAccept');
  AcceptBox.OnClick := @AcceptClick;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = NeedPage.ID) and not (NeedDotNet or NeedRuntime);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = NeedPage.ID then WizardForm.NextButton.Enabled := AcceptBox.Checked;
end;

{ before the installation (after the consent above): what is missing is downloaded from Microsoft and installed }
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID <> wpReady then Exit;
  if not (NeedDotNet or NeedRuntime) then Exit;
  DownloadPage.Clear;
  if NeedDotNet then DownloadPage.Add(DotNetUrl, 'dotnet-runtime.exe', '');
  if NeedRuntime then DownloadPage.Add(AppRuntimeUrl, 'windowsappruntimeinstall.exe', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      SuppressibleMsgBox(AddPeriod(GetExceptionMessage), mbCriticalError, MB_OK, IDOK);
      { no download (no internet): a quiet install goes on, the app asks for what is missing when it starts }
      Result := WizardSilent;
      Exit;
    end;
    if NeedDotNet then
    begin
      DownloadPage.SetText(FmtMessage(CustomMessage('Prereq'), ['.NET 10 Runtime']), '');
      if not RunElevated(ExpandConstant('{tmp}\dotnet-runtime.exe'), '/install /quiet /norestart') or not HasDotNet then
      begin
        SuppressibleMsgBox(FmtMessage(CustomMessage('PrereqFailed'), ['.NET 10 Runtime (' + Arch + ')', 'https://dotnet.microsoft.com/download/dotnet/10.0']), mbError, MB_OK, IDOK);
        { a quiet install goes on without it: Windows asks for it at the start }
        Result := WizardSilent;
        Exit;
      end;
    end;
    if NeedRuntime then
    begin
      DownloadPage.SetText(FmtMessage(CustomMessage('Prereq'), ['Windows App Runtime 2.6']), '');
      if not RunPlain(ExpandConstant('{tmp}\windowsappruntimeinstall.exe'), '--quiet') or not HasAppRuntime then
      begin
        SuppressibleMsgBox(FmtMessage(CustomMessage('PrereqFailed'), ['Windows App Runtime 2.6 (' + Arch + ')', 'https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads']), mbError, MB_OK, IDOK);
        { a quiet install goes on without it: Windows offers it when HeMiDock starts }
        Result := WizardSilent;
        Exit;
      end;
    end;
  finally
    DownloadPage.Hide;
  end;
end;


// Uninstalling: the settings of the app (%AppData%\HeMi\HeMiDock) are deleted or kept, as the user chooses (a quiet
// uninstall keeps them). %AppData%\HeMi goes too when no other HeMi app has anything there.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Dir: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  Dir := ExpandConstant('{userappdata}\HeMi\HeMiDock');
  if not DirExists(Dir) then Exit;
  if UninstallSilent then Exit;
  if MsgBox('Delete the settings of HeMiDock too?' + #13#10#13#10 + 'Keep them to use them again when you install HeMiDock later.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
  begin
    DelTree(Dir, True, True, True);
    RemoveDir(ExpandConstant('{userappdata}\HeMi'));
  end;
end;
