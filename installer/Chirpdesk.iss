; Chirpdesk per-user installer. Build with scripts/pack-release.ps1.
; Prerequisite checks are intentionally honest: a missing runtime gets the official link and
; the setup stops, rather than installing an app that cannot start.

#define AppName "Chirpdesk"
#define AppPublisher "Chirpdesk"
#define AppExe "Chirpdesk.exe"
#define AppId "{{8E4B0E2A-4D5A-4C3B-9E0A-2B7F5C1D9A31}"

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist
OutputBaseFilename=Chirpdesk-Setup-{#AppVersion}
SetupIconFile=..\src\Chirpdesk\Assets\chirpdesk.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
RestartApplications=no
AppMutex=Local\Chirpdesk.Single
SetupMutex=Chirpdesk.Setup
DisableWelcomePage=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
Source: "..\dist\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "Chirpdesk"; Comment: "Warm chat clients in the tray"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "Chirpdesk"; Tasks: desktopicon; Comment: "Warm chat clients in the tray"

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: RelaunchRequested

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
const
  WebView2Download = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';
  DotNet8Download = 'https://dotnet.microsoft.com/download/dotnet/8.0';

function HasDotNet8DesktopAt(const Root: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(Root + '\8.*', FindRec);
  if Result then
    FindClose(FindRec);
end;

function IsDotNet8DesktopInstalled: Boolean;
begin
  Result :=
    HasDotNet8DesktopAt(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App')) or
    HasDotNet8DesktopAt(ExpandConstant('{localappdata}\Microsoft\dotnet\shared\Microsoft.WindowsDesktop.App'));
end;

function HasWebView2Client(const Key: String): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  ClientName: String;
begin
  Result := False;
  if not RegGetSubkeyNames(HKLM, Key, Names) then
    Exit;

  for I := 0 to GetArrayLength(Names) - 1 do
  begin
    if RegQueryStringValue(HKLM, Key + '\' + Names[I], 'name', ClientName)
       and (Pos('WebView2', ClientName) > 0) then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function IsWebView2Installed: Boolean;
begin
  Result :=
    HasWebView2Client('SOFTWARE\Microsoft\EdgeUpdate\Clients') or
    HasWebView2Client('SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients');
end;

function OpenLink(const Url: String): Boolean;
var
  ErrorCode: Integer;
begin
  Result := ShellExec('open', Url, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

function RelaunchRequested: Boolean;
begin
  Result := WizardSilent and (Pos('/RELAUNCH', Uppercase(GetCmdTail)) > 0);
end;

function InitializeSetup: Boolean;
begin
  Result := True;

  if not IsDotNet8DesktopInstalled then
  begin
    if MsgBox('Chirpdesk needs the .NET 8 Desktop Runtime.' + #13#10#13#10 +
              'Open the official download page now? The app cannot start until it is installed.',
              mbError, MB_YESNO) = IDYES then
      OpenLink(DotNet8Download);

    Result := False;
    Exit;
  end;

  if not IsWebView2Installed then
  begin
    if MsgBox('Chirpdesk needs the Microsoft Edge WebView2 Runtime.' + #13#10#13#10 +
              'Open the official download page now? The app cannot start until it is installed.',
              mbError, MB_YESNO) = IDYES then
      OpenLink(WebView2Download);

    Result := False;
    Exit;
  end;
end;

procedure RecycleDirectory(const Directory: String);
var
  Command: String;
  ResultCode: Integer;
begin
  if not DirExists(Directory) then
    Exit;

  Command := '-NoProfile -NonInteractive -Command "Add-Type -AssemblyName Microsoft.VisualBasic; ' +
    '[Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory(''' + Directory +
    ''', ''OnlyErrorDialogs'', ''SendToRecycleBin'')"';

  Exec('powershell.exe', Command, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  if UninstallSilent then
    Exit;

  if MsgBox('Also delete your Chirpdesk data?' + #13#10#13#10 +
            'This removes settings, chat profiles and logs from this user account. ' +
            'They are sent to the Recycle Bin, so they can be restored. ' +
            'Choose No to keep them for a later reinstall.',
            mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
  begin
    RecycleDirectory(ExpandConstant('{userappdata}\Chirpdesk'));
    RecycleDirectory(ExpandConstant('{localappdata}\Chirpdesk'));
  end;
end;
