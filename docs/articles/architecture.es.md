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
| `Views/` | Las ventanas/diálogos XAML y su code-behind ligero. |
| `Localization/` | El sistema de traducción (`Localizer` + la extensión de marcado `{loc:Loc}`). |
| `Behaviors/`, `Converters/` | Pequeñas ayudas de la interfaz (auto-scroll, color del MOTD, bool→visibilidad). |
| `Resources/` | `Strings*.resx` (traducciones) y `app.ico`. |

Los datos se guardan **por usuario** en `%APPDATA%\McServerLauncher\`: `servers.json` (la lista de
servidores), `settings.json` (ajustes globales) y `java\` (versiones de Java que instala la app). No
hay rutas fijas del equipo en el código.

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
- **`UpdateService`** — comprueba en las Releases de GitHub si hay versión más nueva y descarga el
  instalador para la actualización dentro de la app.

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
`AppSettings.LastVersionSeen` y muestra `WhatsNewDialog` (traducido) cuando ha cambiado.

## Localización

Todo el texto visible está en `Resources/Strings.resx` (español, idioma neutral/base) más los
archivos satélite `Strings.en.resx`, `Strings.pt.resx`, `Strings.fr.resx`, `Strings.de.resx`. El
código los lee con `Localizer.Get("Clave")` (y `string.Format` para parámetros); el XAML usa la
extensión de marcado `{loc:Loc Clave}`. El idioma activo viene de `AppSettings.Language` y se aplica
en `App.OnStartup` antes de crear ninguna ventana, por eso cambiar de idioma requiere reiniciar.
Mira [Cómo contribuir](contributing.es.md) para añadir un idioma o un texto nuevo.
