; MayhemDeck installer (Inno Setup 6). Built by build-installer.ps1, which fills build\app first.
; Installs per user (no admin prompt) to %LOCALAPPDATA%\Programs\MayhemDeck.
; MayhemDeck was called GiftDeck: the AppId is the same, so installing it updates an existing GiftDeck in place
; (same folder, settings kept in %APPDATA%\GiftDeck) and the old shortcuts and exe are removed.

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

[Setup]
AppId={{6F2C8B1E-4D7A-4E9B-9C3F-2A61D0B7E5C4}
AppName=MayhemDeck
AppVersion={#MyAppVersion}
AppVerName=MayhemDeck {#MyAppVersion}
AppPublisher=Obiwayne
AppPublisherURL=https://github.com/Obiwayne/MayhemDeck
AppSupportURL=https://github.com/Obiwayne/MayhemDeck/issues
DefaultDirName={localappdata}\Programs\MayhemDeck
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=build
OutputBaseFilename=MayhemDeck-Setup-{#MyAppVersion}
SetupIconFile=Assets\giftdeck.ico
UninstallDisplayIcon={app}\MayhemDeck.exe
UninstallDisplayName=MayhemDeck
LicenseFile=LICENSE
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; MayhemDeck holds this mutex while open (the name is from when it was GiftDeck), so Setup asks to close it first.
AppMutex=GiftDeck.SingleInstance
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Put a MayhemDeck shortcut on the Desktop"; GroupDescription: "Shortcuts:"

[Files]
Source: "build\app\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[InstallDelete]
; left over from when it was called GiftDeck
Type: files; Name: "{app}\GiftDeck.exe"
Type: filesandordirs; Name: "{app}\GiftDeck.exe.WebView2"
Type: files; Name: "{autoprograms}\GiftDeck.lnk"
Type: files; Name: "{autodesktop}\GiftDeck.lnk"

[UninstallDelete]
; files MayhemDeck creates in its own folder while running (settings stay in %APPDATA%\GiftDeck)
Type: files; Name: "{app}\bridge\*.log"
Type: filesandordirs; Name: "{app}\MayhemDeck.exe.WebView2"
Type: dirifempty; Name: "{app}\bridge"
Type: dirifempty; Name: "{app}"

[Icons]
Name: "{autoprograms}\MayhemDeck"; Filename: "{app}\MayhemDeck.exe"
Name: "{autodesktop}\MayhemDeck"; Filename: "{app}\MayhemDeck.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MayhemDeck.exe"; Description: "Open MayhemDeck"; Flags: nowait postinstall skipifsilent

[Code]
// The bridge's node.exe runs from {app}\bridge. If one was left running (e.g. MayhemDeck crashed),
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
