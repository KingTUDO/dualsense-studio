#define AppVersion "0.1.3"
#ifndef PayloadDir
  #error Informe o caminho do pacote publicado com /DPayloadDir
#endif

[Setup]
AppId={{35B83F06-05F8-4D60-855C-12041F5C836F}
AppName=DualSense Studio
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\DualSense Studio
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
UninstallDisplayIcon={app}\DualSense Studio.exe
OutputBaseFilename=DualSense-Studio-Setup-{#AppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PayloadDir}\DualSense Studio.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\LEIA-ME.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "Licencas\*"; DestDir: "{app}\Licencas"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\DualSense Studio"; Filename: "{app}\DualSense Studio.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\DualSense Studio.exe"; Description: "{cm:LaunchProgram,DualSense Studio}"; Flags: nowait postinstall skipifsilent
