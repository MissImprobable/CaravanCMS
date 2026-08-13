#define MyAppName "CaravanCMS Admin"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "CaravanCMS"
#define MyAppExeName "CaravanCMS.Admin.exe"

[Setup]
AppId={{9C1B3B9C-6F1E-4C9A-9E2E-CARAVANADMIN0}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\CaravanCMS Admin
DefaultGroupName=CaravanCMS Admin
DisableProgramGroupPage=yes
OutputDir=..\
OutputBaseFilename=CaravanCMS-Admin-v{#MyAppVersion}-setup
SetupIconFile=..\CaravanCMS.Admin\Resources\admin.ico
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\Admin\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
