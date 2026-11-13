; HeMiDock installer (Inno Setup 6). Built by release.ps1:  ISCC /DAppVersion=1.5.0 /DSourceDir=<published app> /DOutputDir=<folder>
; Installs for the current user (no administrator) or for all users (asked), checks the .NET 10 Runtime and the Windows App Runtime 2.6 and installs
; what is missing from Microsoft (only that asks for administrator rights). /UPDATE (the update of the app): quiet, starts the
; dock again afterwards.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\build\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\build\installer"
#endif

[Setup]
AppId=HeMiDock
AppName=HeMiDock
AppVersion={#AppVersion}
AppVerName=HeMiDock Beta {#AppVersion}
AppPublisher=Richard Hemala
AppPublisherURL=https://github.com/Hemal1k/HeMiDock
AppSupportURL=https://github.com/Hemal1k/HeMiDock/issues
AppUpdatesURL=https://github.com/Hemal1k/HeMiDock/releases
DefaultDirName={autopf}\HeMiDock
DisableDirPage=yes
DisableProgramGroupPage=yes
DefaultGroupName=HeMiDock
PrivilegesRequired=lowest
; the first page asks: for me only (default, no administrator) or for all users (Program Files, asks for administrator)
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=HeMiDock-Setup
SetupIconFile=..\src\Assets\HeMiDock.ico
UninstallDisplayIcon={app}\HeMiDock.exe
UninstallDisplayName=HeMiDock
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
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
en.NeedTitle=Components from Microsoft
en.NeedDesc=HeMiDock needs components that are missing on this PC.
en.NeedText=The installer downloads them directly from Microsoft and installs them (Windows asks for administrator rights). They come with Microsoft's license terms – please read them before you continue:
en.NeedLicense=license terms
en.NeedAccept=I accept the license terms of these components and agree to download and install them

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
; offered on its own page, off by default
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Icons]
Name: "{autoprograms}\HeMiDock"; Filename: "{app}\HeMiDock.exe"
Name: "{autodesktop}\HeMiDock"; Filename: "{app}\HeMiDock.exe"; Tasks: desktopicon

[Run]
; after an interactive installation: offered; after an update (quiet): the dock starts again by itself
; (as the user who started the installer – never as administrator after an installation for all users)
Filename: "{app}\HeMiDock.exe"; Description: "{cm:LaunchAfter}"; Flags: nowait postinstall skipifsilent runasoriginaluser
Filename: "{app}\HeMiDock.exe"; Parameters: "--after-update"; Flags: nowait runasoriginaluser; Check: WizardSilent

[UninstallRun]
Filename: "{app}\HeMiDock.exe"; Parameters: "--uninstall"; Flags: waituntilterminated; RunOnceId: "HeMiDockUninstall"

[Code]
const
  DotNetUrl = 'https://aka.ms/dotnet/10.0/dotnet-runtime-win-x64.exe';
  AppRuntimeUrl = 'https://aka.ms/windowsappsdk/2.6/latest/windowsappruntimeinstall-x64.exe';
  DotNetLicense = 'https://aka.ms/dotnet-license';
  AppRuntimeLicense = 'https://www.nuget.org/packages/Microsoft.WindowsAppSDK.Runtime/2.6.5/License';

var
  DownloadPage: TDownloadWizardPage;
  NeedPage: TWizardPage;
  AcceptBox: TNewCheckBox;
  NeedDotNet, NeedRuntime: Boolean;

{ the .NET 10 Runtime (x64): a folder 10.x of Microsoft.NETCore.App }
function HasDotNet: Boolean;
var
  Found: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.NETCore.App\10.*'), Found);
  if Result then FindClose(Found);
end;

{ the Windows App Runtime 2.6 or newer (x64), asked by PowerShell }
function HasAppRuntime: Boolean;
var
  Code: Integer;
begin
  Result := Exec('powershell.exe',
    '-NoProfile -ExecutionPolicy Bypass -Command "if (Get-AppxPackage -Name Microsoft.WindowsAppRuntime.2 | Where-Object { $_.Architecture -eq ''X64'' -and $_.Version -ge [version]''2.6.0.0'' }) { exit 0 } else { exit 1 }"',
    '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

{ the running dock ends before its files are replaced (it gives the taskbar of Windows back) }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\HeMiDock.exe')) then
  begin
    Exec(ExpandConstant('{app}\HeMiDock.exe'), '--exit', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Sleep(1500);
  end;
end;

function RunElevated(const FileName, Params: String): Boolean;
var
  Code: Integer;
begin
  Result := ShellExec('runas', FileName, Params, '', SW_SHOW, ewWaitUntilTerminated, Code) and ((Code = 0) or (Code = 3010));
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
  if NeedDotNet then Y := AddComponent('.NET 10 Runtime (x64)', DotNetLicense, Y);
  if NeedRuntime then Y := AddComponent('Windows App Runtime 2.6 (x64)', AppRuntimeLicense, Y);
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
  if NeedDotNet then DownloadPage.Add(DotNetUrl, 'dotnet-runtime-win-x64.exe', '');
  if NeedRuntime then DownloadPage.Add(AppRuntimeUrl, 'windowsappruntimeinstall-x64.exe', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      SuppressibleMsgBox(AddPeriod(GetExceptionMessage), mbCriticalError, MB_OK, IDOK);
      Result := False;
      Exit;
    end;
    if NeedDotNet then
    begin
      DownloadPage.SetText(FmtMessage(CustomMessage('Prereq'), ['.NET 10 Runtime']), '');
      if not RunElevated(ExpandConstant('{tmp}\dotnet-runtime-win-x64.exe'), '/install /quiet /norestart') or not HasDotNet then
      begin
        SuppressibleMsgBox(FmtMessage(CustomMessage('PrereqFailed'), ['.NET 10 Runtime (x64)', 'https://dotnet.microsoft.com/download/dotnet/10.0']), mbError, MB_OK, IDOK);
        Result := False;
        Exit;
      end;
    end;
    if NeedRuntime then
    begin
      DownloadPage.SetText(FmtMessage(CustomMessage('Prereq'), ['Windows App Runtime 2.6']), '');
      if not RunElevated(ExpandConstant('{tmp}\windowsappruntimeinstall-x64.exe'), '--quiet') or not HasAppRuntime then
      begin
        SuppressibleMsgBox(FmtMessage(CustomMessage('PrereqFailed'), ['Windows App Runtime 2.6 (x64)', 'https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads']), mbError, MB_OK, IDOK);
        Result := False;
        Exit;
      end;
    end;
  finally
    DownloadPage.Hide;
  end;
end;
