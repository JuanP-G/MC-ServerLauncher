; Instalador de MC Server Launcher (Inno Setup 6)
; Genera un instalador que crea accesos directos en el Escritorio y el menú Inicio.
; Antes de compilar este script hay que publicar la app (ver publish.ps1).

#define MyAppName "MC Server Launcher"
; publish.ps1 pasa la versión real con /DMyAppVersion=... (leída del .csproj, fuente única de verdad).
; Este valor por defecto solo se usa si se compila el .iss a mano sin ese parámetro.
#ifndef MyAppVersion
  #define MyAppVersion "1.12.1"
#endif
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
; Solo se crea si el usuario lo pide y aún no hay ninguno. Una actualización NO lo vuelve a crear:
; ver ShouldCreateDesktopIcon.
Name: "{autodesktop}\MC Server Launcher"; Filename: "{app}\{#MyAppExeName}"; Check: ShouldCreateDesktopIcon

[Run]
; Refresca la caché de iconos de Windows para que el acceso directo muestre el icono nuevo tras
; una actualización (como usuario normal: la caché de iconos es por usuario).
Filename: "{sys}\ie4uinit.exe"; Parameters: "-show"; Flags: runhidden runasoriginaluser
; Sin skipifsilent y con runasoriginaluser: tras una actualización silenciosa desde la app, se
; relanza automáticamente como usuario normal (no elevado). En instalación normal es la casilla final.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,MC Server Launcher}"; Flags: nowait postinstall runasoriginaluser

[Code]
// El instalador escribe en autodesktop (con privilegios de admin, el escritorio comun), pero el
// boton "Anadir al escritorio" de la propia app escribe en el escritorio del usuario. Cualquiera
// de los dos cuenta como "ya tiene un acceso directo".
function DesktopIconExists: Boolean;
begin
  Result := FileExists(ExpandConstant('{autodesktop}\MC Server Launcher.lnk')) or
            FileExists(ExpandConstant('{userdesktop}\MC Server Launcher.lnk'));
end;

// Si ya hay un acceso directo, no se toca.
//
// Recrearlo no es "sobrescribirlo": Inno borra el .lnk y lo vuelve a crear, y Windows guarda la
// posicion de cada icono del escritorio indexada por el nombre del fichero. Al desaparecer el
// fichero se pierde su posicion, y el nuevo aparece en el primer hueco libre: por eso el
// escritorio se reordenaba entero en cada actualizacion.
//
// No hace falta recrearlo para refrescar el icono. La ruta de instalacion es fija, de modo que el
// acceso directo que ya existe sigue apuntando al sitio correcto y saca el icono del .exe nuevo;
// de refrescar la cache se encarga el ie4uinit de la seccion Run.
//
// WizardSilent cubre el caso que queda: si el usuario habia borrado el icono a proposito,
// una actualizacion desde la propia app (que lanza el instalador con /SILENT) tampoco se lo
// devuelve. Dicho de otro modo: una instalacion silenciosa nunca toca el escritorio.
function ShouldCreateDesktopIcon: Boolean;
begin
  Result := WizardIsTaskSelected('desktopicon') and not WizardSilent and
            not DesktopIconExists;
end;
