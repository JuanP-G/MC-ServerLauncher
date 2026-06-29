; Instalador de MC Server Launcher (Inno Setup 6)
; Genera un instalador que crea accesos directos en el Escritorio y el menú Inicio.
; Antes de compilar este script hay que publicar la app (ver publish.ps1).

#define MyAppName "MC Server Launcher"
#define MyAppVersion "1.0.3"
#define MyAppPublisher "JuanP-G"
#define MyAppURL "https://github.com/JuanP-G/MC-ServerLauncher"
#define MyAppExeName "McServerLauncher.exe"
#define PublishDir "..\McServerLauncher\bin\Release\net9.0-windows\win-x64\publish"

[Setup]
AppId={{B8E7A3C1-2F4D-4A9B-9C1E-7D5F6A8B0C23}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\MC Server Launcher
DefaultGroupName=MC Server Launcher
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=MC-ServerLauncher-Setup-{#MyAppVersion}
SetupIconFile=..\McServerLauncher\Resources\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
; Para la actualización desde la app: cierra la app en uso y NO la relanza el propio Restart Manager
; (la relanzamos nosotros en [Run] como usuario normal).
CloseApplications=force
RestartApplications=no

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\MC Server Launcher"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Desinstalar MC Server Launcher"; Filename: "{uninstallexe}"
Name: "{autodesktop}\MC Server Launcher"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Sin skipifsilent y con runasoriginaluser: tras una actualización silenciosa desde la app, se
; relanza automáticamente como usuario normal (no elevado). En instalación normal es la casilla final.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,MC Server Launcher}"; Flags: nowait postinstall runasoriginaluser
