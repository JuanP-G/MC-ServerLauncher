# Arquitectura

> 🇬🇧 Prefer English? Read the [English version](architecture.md).

MC Server Launcher es una app de escritorio en **Avalonia / .NET 9** que sigue el patrón **MVVM**
(con [CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/)) y el tema
Fluent de [Avalonia](https://avaloniaui.net/) (multiplataforma). Gestiona uno o varios servidores de Minecraft sin
archivos `.bat`, ventanas de consola ni editar configuración a mano.

## Capas

El proyecto (`McServerLauncher/`) está organizado por responsabilidad:

| Carpeta | Responsabilidad |
|---|---|
| `Models/` | Datos puros: configuración persistida (`ServerConfig`), ajustes (`AppSettings`), enums (`ServerState`, `PlayitState`). |
| `Services/` | Toda la lógica sin interfaz: procesos, archivos, red, Java, Playit, puertos, etc. Cada servicio es una clase pequeña y centrada. |
| `ViewModels/` | El estado y los comandos a los que se enlaza la interfaz (`MainViewModel`, `ServerViewModel`). Aquí no hay controles de Avalonia, solo `ObservableObject`/`RelayCommand`. |
| `Views/` | Las ventanas/diálogos `.axaml` (XAML de Avalonia) y su code-behind ligero. |
| `Localization/` | El sistema de traducción (`Localizer` + la extensión de marcado `{loc:Loc}`). |
| `Behaviors/` | Comportamientos adjuntos (`AutoScrollBehavior`, color del MOTD en `MinecraftMotd`). |
| `Controls/` | Controles propios (`Sparkline` para las mini-gráficas de CPU/RAM). |
| `Resources/` | `Strings*.resx` (traducciones) y `app.ico`. |

> El único conversor de valores, `BoolOpacityConverter`, vive en `ViewModels/` — no existe una
> carpeta `Converters/`.

Los datos se guardan **por usuario** en `%APPDATA%\McServerLauncher\`:

- `servers.json` — la lista de servidores y la configuración de cada uno.
- `settings.json` — ajustes globales (idioma, clave de Playit, última versión vista…).
  Ambos JSON se escriben de forma **atómica** (`AtomicJsonFile`): la versión anterior se conserva
  como `.bak`, y un archivo corrupto se aparta como `.bad` y se recupera desde el `.bak` cuando es
  posible (avisando al usuario al arrancar en vez de perder la lista en silencio).
- `java\` — las versiones de Java que instala la app (Temurin/Adoptium).
- `logs\` — el log de consola persistente (`launcher-yyyy-MM-dd.log`, se poda a los 14 días).
- `.secret.key` — la clave AES-GCM que cifra los secretos en Linux/macOS (Windows usa DPAPI, así que
  ahí no hay archivo de clave).

Además, la carpeta de cada servidor contiene un directorio `backups\` con las copias automáticas del
mundo. No hay rutas fijas del equipo en el código.

## Servicios clave

- **`ServerProcessManager`** — gestiona el ciclo de vida del proceso `java`: lo arranca (sin ventana
  de consola), redirige stdin/stdout/stderr, reemite cada línea por un evento y lo detiene de forma
  limpia enviando `stop` (con kill de respaldo).
- **`JavaService`** — detecta los Java instalados y, si ninguno es compatible, descarga el JRE
  Temurin (Adoptium) adecuado para la arquitectura. Se usa al crear y al iniciar un servidor.
- **`MinecraftVersionService`** — lee el manifiesto de versiones de Mojang, resuelve la URL del
  `server.jar` y la versión de Java necesaria, y descarga archivos.
- **`PlayitApiService`** / **`PlayitManager`** — hablan con Playit.gg: leen la dirección pública del
  túnel (con la clave de solo lectura del agente) y crean/eliminan túneles (con una clave de
  escritura del usuario); `PlayitManager` consulta/arranca/detiene el servicio de Windows de fondo.
- **`PortService`** — comprueba qué puertos TCP están en uso, encuentra uno libre y (vía P/Invoke)
  localiza el PID que escucha en un puerto para liberar un servidor colgado.
- **`ServerPropertiesService`**, **`PlayersService`**, **`WhitelistService`** — leen/escriben los
  archivos del servidor (`server.properties`, `ops.json`, `banned-players.json`, `whitelist.json`).
- **`ServerCreationService`** — escribe los archivos iniciales de un servidor nuevo: `eula.txt`,
  `run.bat`/`user_jvm_args.txt` y el `server.properties` mínimo con el puerto elegido. (La descarga
  del jar la hacen `MinecraftVersionService`/`ModLoaderService`/`PaperService` y el puerto lo elige
  `PortService`, todo orquestado por `CreateServerDialog`.)
- **`ModLoaderService`** / **`PaperService`** — instalan un mod loader (Fabric/Forge) o un build de
  Paper sobre un servidor existente, conservando el mundo.
- **`ModrinthService`** — busca en Modrinth y descarga mods/plugins (filtrados por el tipo y la
  versión del servidor), y gestiona el flujo de "buscar actualizaciones de mods".
- **`ServerDetectionService`** — inspecciona una carpeta para averiguar el tipo/versión de un servidor
  existente cuando el usuario añade uno que ya está.
- **`ServerIconService`** — genera el `server-icon.png` de un servidor: toma cualquier imagen del
  usuario, la recorta al cuadrado centrado y la escala a 64×64 con SkiaSharp. (Quien lo lee de vuelta
  para la vista estilo Minecraft es `ServerViewModel.LoadIcon`.)
- **`WorldBackupService`** — crea y restaura copias zip del mundo del servidor
  (`<servidor>/backups/`), podando las antiguas según la retención.
- **`CrashReportService`** — lee `crash-reports/*.txt` para extraer la línea `Description:` y mostrar
  un motivo legible del crash. (La detección del cierre inesperado es el evento `UnexpectedExit` de
  `ServerProcessManager`; la lógica de auto-reinicio vive en `ServerViewModel`.)
- **`ConsoleLogService`** — copia cada línea de consola a `%APPDATA%\McServerLauncher\logs\` para que
  el historial sobreviva a los reinicios (retención de 14 días).
- **`ProcessStatsService`** — muestrea CPU/RAM del proceso `java` en marcha para las estadísticas en
  vivo y las mini-gráficas `Sparkline`.
- **`ToastService`** — muestra notificaciones emergentes propias — ventanas de Avalonia siempre
  encima en la esquina inferior derecha (jugador entra, servidor caído, reinicio agotado); funcionan
  aunque el SO no soporte notificaciones.
- **`SecretProtector`** — cifra los secretos en reposo (DPAPI en Windows, AES-GCM + `.secret.key` en
  Linux/macOS), usado para la clave de escritura de Playit.
- **`DownloadVerifier`** — el verificador de checksums compartido para las descargas (Mojang SHA-1,
  Adoptium/Paper SHA-256, Modrinth SHA-512/SHA-1), que borra el archivo si no cuadra.
- **`Changelog`** — las notas de "novedades" por versión que se muestran tras actualizar (ver el
  flujo más abajo).
- **`UpdateService`** — comprueba en las Releases de GitHub si hay versión más nueva y descarga el
  instalador para la actualización dentro de la app. La verificación contra el asset
  `SHA256SUMS.txt` de la release es **obligatoria**: si el checksum falta o no se puede leer, la
  instalación silenciosa se rechaza y se abre la página de la release en su lugar.

## Flujos importantes

### Arrancar un servidor
`ServerViewModel.Start` → refresca puerto/info → si el puerto está ocupado, ofrece liberarlo
(`PortService` + `TryFreePortAsync`) → `EnsureCompatibleJavaAsync` (usa `JavaService` para leer el
Java requerido del jar e instalarlo si hace falta) → `ServerProcessManager.Start`. La salida de la
consola llega de vuelta por el evento `OutputReceived` hacia `ConsoleLines`.

### Java automático
Al **crear**, `CreateServerDialog` pide a `MinecraftVersionService` el Java necesario y llama a
`JavaService.EnsureJavaAsync`. Al **iniciar**, `ServerViewModel` lee la versión de Java embebida en
`server.jar` (`version.json`) e instala/usa un runtime compatible, guardando la ruta en
`ServerConfig.JavaPath`.

### Túnel de Playit
Al crear un servidor (o con el botón "Crear túnel"), `MainViewModel` llama a
`PlayitApiService.EnsureMinecraftTunnelAsync` con la clave de escritura. La dirección pública la
detecta periódicamente `ServerViewModel` con `GetAddressForPortAsync`, emparejando por puerto local.

### Actualización in-app + novedades
Al arrancar, `MainViewModel.CheckForUpdatesAsync` pide a `UpdateService` la última release y su
instalador. El botón **Actualizar** (`UpdateNowCommand`) descarga el instalador, detiene servidores,
lo ejecuta en silencio y sale; el instalador reinstala y relanza la app. Tras actualizar,
`MainWindow.Loaded` llama a `ShowWhatsNewIfUpdated`, que compara la versión en ejecución con
`AppSettings.LastVersionSeen` y muestra `WhatsNewDialog` (traducido) con las notas de `Changelog` de
cada versión que el usuario aún no había visto.

### Copias del mundo (backups)
`WorldBackupService` zipea el mundo de un servidor en `<servidor>/backups/` a demanda y de forma
automática: antes de cada arranque (la red de seguridad principal — cubre también Restart y el
auto-reinicio tras un crash), después de un stop manual limpio, y antes de restaurar. Conserva las
más recientes hasta la retención configurada. `ServerBackupsView` las lista y puede restaurar
cualquiera (tomando antes una copia de seguridad por si acaso).

### Auto-reinicio tras un crash
Cuando un servidor se cierra inesperadamente, `ServerProcessManager` emite su evento `UnexpectedExit`
y `ServerViewModel` lo reinicia con un presupuesto (unos pocos intentos dentro de una ventana de
estabilidad) para evitar bucles de crash, avisando al usuario con `ToastService` si el presupuesto se
agota. `CrashReportService` lee el crash report del servidor para añadir un motivo legible a esa
notificación.

### Bandeja del sistema
`App` instala un `TrayIcon`. Minimizar mantiene la ventana en la barra de tareas como siempre;
cerrarla con la **X** la oculta a la bandeja (los servidores siguen corriendo) en vez de salir. El
menú de la bandeja restaura la ventana (**Mostrar**) o cierra de verdad (**Salir** →
`MainWindow.RequestExit`, que hace el apagado limpio).

### Buscar actualizaciones de mods/plugins
`ServerModsViewModel` pide a `ModrinthService` identificar cada archivo instalado en Modrinth y marcar
los que tienen una versión más nueva; el usuario actualiza cada uno con un clic (descarga verificada
con checksum vía `DownloadVerifier`, conservando su estado activado/desactivado).

## Localización

Todo el texto visible está en `Resources/Strings.resx` (español, idioma neutral/base) más los
archivos satélite `Strings.en.resx`, `Strings.pt.resx`, `Strings.fr.resx`, `Strings.de.resx`. El
código los lee con `Localizer.Get("Clave")` (y `string.Format` para parámetros); el XAML usa la
extensión de marcado `{loc:Loc Clave}`. El idioma activo viene de `AppSettings.Language` y se aplica
en `App.OnFrameworkInitializationCompleted` antes de crear ninguna ventana, por eso cambiar de idioma
requiere reiniciar.
Mira [Cómo contribuir](contributing.es.md) para añadir un idioma o un texto nuevo.
