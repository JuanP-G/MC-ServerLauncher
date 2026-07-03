; Instalador de MC Server Launcher (Inno Setup 6)
; Genera un instalador que crea accesos directos en el Escritorio y el menú Inicio.
; Antes de compilar este script hay que publicar la app (ver publish.ps1).

#define MyAppName "MC Server Launcher"
#define MyAppVersion "1.2.4"
#define MyAppPublisher "JuanP-G"
#define MyAppURL "https://github.com/JuanP-G/MC-ServerLauncher"
#define MyAppExeName "McServerLauncher.exe"
#define PublishDir "..\McServerLauncher\bin\Release\net9.0\win-x64\publish"

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
; Inglés por defecto (no auto-detectar el idioma del sistema). El usuario puede cambiar a español
; en el diálogo de idioma que aparece al inicio del asistente.
LanguageDetectionMethod=none

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

; Remove any previous install contents first, so upgrading from the old WPF build leaves no orphan
; files behind (user data lives in %APPDATA%, not here, so this is safe).
[InstallDelete]
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\MC Server Launcher"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,MC Server Launcher}"; Filename: "{uninstallexe}"
; Recrea el acceso directo del escritorio si el usuario lo marca O si ya tenía uno (así una
; actualización refresca su icono aunque no se vuelva a marcar la casilla).
Name: "{autodesktop}\MC Server Launcher"; Filename: "{app}\{#MyAppExeName}"; Check: ShouldCreateDesktopIcon

[Run]
; Refresca la caché de iconos de Windows para que el acceso directo muestre el icono nuevo tras
; una actualización (como usuario normal: la caché de iconos es por usuario).
Filename: "{sys}\ie4uinit.exe"; Parameters: "-show"; Flags: runhidden runasoriginaluser
; Sin skipifsilent y con runasoriginaluser: tras una actualización silenciosa desde la app, se
; relanza automáticamente como usuario normal (no elevado). En instalación normal es la casilla final.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,MC Server Launcher}"; Flags: nowait postinstall runasoriginaluser

[Code]
function ShouldCreateDesktopIcon: Boolean;
begin
  Result := WizardIsTaskSelected('desktopicon') or
            FileExists(ExpandConstant('{autodesktop}\MC Server Launcher.lnk'));
end;
