; Inno Setup 6 script. Build with:  .\publish.ps1 -Installer
#define AppName "NextGen Island"
#define AppVersion "1.0.0"
#define AppExe "NextGenIsland.exe"

[Setup]
AppId={{6B7C2E5A-3D41-4F5B-9B0E-5A1C7E9D2F10}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=NextGen Island
DefaultDirName={autopf}\NextGenIsland
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts
OutputBaseFilename=NextGenIsland-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}
CloseApplications=yes

[Files]
Source: "..\artifacts\publish\*"; DestFlags: ignoreversion recursesubdirs createallsubdirs; DestDir: "{app}"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start NextGen Island"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; User data (clipboard history, settings) stays in %LOCALAPPDATA%\NextGenIsland on purpose.
Type: filesandordirs; Name: "{app}"
