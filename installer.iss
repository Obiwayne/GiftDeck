; GiftDeck installer (Inno Setup 6). Built by build-installer.ps1, which fills build\app first.
; Installs per user (no admin prompt) to %LOCALAPPDATA%\Programs\GiftDeck.

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

[Setup]
AppId={{6F2C8B1E-4D7A-4E9B-9C3F-2A61D0B7E5C4}
AppName=GiftDeck
AppVersion={#MyAppVersion}
AppVerName=GiftDeck {#MyAppVersion}
AppPublisher=Obiwayne
AppPublisherURL=https://github.com/Obiwayne/GiftDeck
AppSupportURL=https://github.com/Obiwayne/GiftDeck/issues
DefaultDirName={localappdata}\Programs\GiftDeck
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=build
OutputBaseFilename=GiftDeck-Setup-{#MyAppVersion}
SetupIconFile=Assets\giftdeck.ico
UninstallDisplayIcon={app}\GiftDeck.exe
UninstallDisplayName=GiftDeck
LicenseFile=LICENSE
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; GiftDeck holds this mutex while open, so Setup asks to close it before updating.
AppMutex=GiftDeck.SingleInstance
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Put a GiftDeck shortcut on the Desktop"; GroupDescription: "Shortcuts:"

[Files]
Source: "build\app\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[UninstallDelete]
; files GiftDeck creates in its own folder while running (settings stay in %APPDATA%\GiftDeck)
Type: files; Name: "{app}\bridge\*.log"
Type: filesandordirs; Name: "{app}\GiftDeck.exe.WebView2"
Type: dirifempty; Name: "{app}\bridge"
Type: dirifempty; Name: "{app}"

[Icons]
Name: "{autoprograms}\GiftDeck"; Filename: "{app}\GiftDeck.exe"
Name: "{autodesktop}\GiftDeck"; Filename: "{app}\GiftDeck.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\GiftDeck.exe"; Description: "Open GiftDeck"; Flags: nowait postinstall skipifsilent

[Code]
// The bridge's node.exe runs from {app}\bridge. If one was left running (e.g. GiftDeck crashed),
// stop it so its files can be replaced or removed. Your settings in %APPDATA%\GiftDeck are kept.
procedure StopBridge(AppDir: String);
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -Command "Get-Process node,ffmpeg -ErrorAction SilentlyContinue | Where-Object { $_.Path -like ''' + AppDir + '\*'' } | Stop-Process -Force"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopBridge(ExpandConstant('{app}'));
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopBridge(ExpandConstant('{app}'));
  Result := True;
end;
