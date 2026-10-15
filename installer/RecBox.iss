; RecBox — Inno Setup installer
; Build with: build-installer.ps1  (or compile this file in Inno Setup)
;
; Upgrades replace program files but never touch %AppData%\RecBox\settings.json

#define MyAppName "RecBox"
#define MyAppPublisher "RecBox"
#define MyAppURL "https://lcarlini.github.io/RecBox/"
#define MyAppExeName "RecBox.exe"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\RecBox"
#endif

[Setup]
AppId={{A7C3E8F1-4B2D-4E9A-9C1F-8D6E5B0A2F34}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={localappdata}\Programs\RecBox
DefaultGroupName=RecBox
DisableProgramGroupPage=yes
LicenseFile=
OutputDir=..\dist\installer
OutputBaseFilename=RecBox-{#MyAppVersion}-Setup
SetupIconFile=..\src\RecBox\Assets\recbox.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}
CloseApplications=yes
RestartApplications=no
; Keep user settings across upgrades/uninstall
; (settings live in %AppData%\RecBox — outside {app})

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch RecBox"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Only remove leftover files under the install folder — never AppData settings
Type: filesandordirs; Name: "{app}\logs"

[Code]
function InitializeUninstall(): Boolean;
begin
  Result := True;
  // Intentionally do NOT delete %AppData%\RecBox so settings survive reinstall/upgrade
end;
