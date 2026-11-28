#define AppName "Downora"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define AppPublisher "R00TAdm1n"
#define AppExeName "Downora.exe"
#ifndef AppArchitecture
  #define AppArchitecture "x64"
#endif
#ifndef OutputBaseName
  #define OutputBaseName "Downora-Setup"
#endif

[Setup]
AppId={{A2C5A2D4-6D31-4A4E-A3D7-7E0D9F2C41B8}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\Downora
DefaultGroupName=Downora
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed={#AppArchitecture}
ArchitecturesInstallIn64BitMode={#AppArchitecture}
OutputDir=output
OutputBaseFilename={#OutputBaseName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExeName}
SetupLogging=yes
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Downora"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\Downora"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
