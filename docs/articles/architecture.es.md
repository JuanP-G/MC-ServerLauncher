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
- `settings.json` — ajustes globales (idioma, clave secreta del agente de Playit, última versión vista…).
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
- **`PlayitApiService`** / **`PlayitPartnerService`** / **`PlayitManager`** — hablan con Playit.gg.
  `PlayitPartnerService` ejecuta el flujo de **código de configuración** de terceros (`create_agent`)
  para obtener una **clave secreta de agente autogestionado por usuario** a partir de un código que
  el usuario pega. La **Api-Key de socio no está en la app** (es pública + open-source): la llamada
  pasa por un pequeño proxy (un Cloudflare Worker, ver `playit-proxy/`) que añade la clave en el
  servidor. El Worker solo acepta el POST de create-agent con la forma que envía la app de
  escritorio, rechaza tráfico con origen de navegador y puede limitar intentos por IP. El
  variant_id/versión son públicos y van incrustados. `PlayitApiService` usa la clave
  por usuario devuelta (como `agent-key`, fijada con `SetAgentKey`) para listar/crear/eliminar
  túneles — con reserva al `playit.toml` heredado o a una clave de escritura pegada. `PlayitManager`
  consulta/arranca/detiene el servicio de fondo (Windows/systemd). `PlayitConnection` es el flujo
  compartido de conectar/desconectar que usan los botones de túnel y el diálogo de Ajustes.
- **`PortService`** — comprueba qué puertos TCP están en uso, encuentra uno libre y (vía P/Invoke)
  localiza el PID que escucha en un puerto para liberar un servidor colgado.
- **`ServerPropertiesService`**, **`PlayersService`**, **`WhitelistService`** — leen/escriben los
  archivos del servidor (`server.properties`, `ops.json`, `banned-players.json`, `whitelist.json`).
- **`ServerCreationService`** — escribe los archivos iniciales de un servidor nuevo: `eula.txt`,
  `run.bat`/`user_jvm_args.txt` y el `server.properties` mínimo con el puerto elegido. (La descarga
  del jar la hacen `MinecraftVersionService`/`ModLoaderService`/`PaperService` y el puerto lo elige
  `PortService`, todo orquestado por `CreateServerDialog`.)
- **`ServerTypeCatalog`** — una fila por tipo de servidor: nombre, familia (plugins/mods/ninguna), color de la
  insignia y su `CrossplayLevel`. El selector, las insignias, la tienda de mods, la carpeta de contenido y las reglas
  de crossplay leen de ahí, así que añadir un tipo es una fila y no seis `switch` repartidos.
  El nivel tiene tres valores en vez de sí/no, porque «Geyser publica una versión» y «tu amigo con el móvil puede
  jugar» no son la misma afirmación: `Full` en Paper, Purpur y Fabric; `Partial` en NeoForge — conecta y autentica,
  y a partir de ahí cualquier mod que el cliente necesite tener deja fuera a Bedrock —; y `None` en Vanilla y
  Forge. `CrossplayService.CaveatKey` convierte el nivel en el aviso que muestran los dos diálogos.
- **`ServerJarInstaller`** — el único sitio que sabe cómo se obtiene cada tipo. Lo llaman el diálogo de creación y el
  de cambiar el tipo; antes la cadena estaba escrita en los dos, y un tipo presente en uno y ausente en el otro
  producía un servidor Vanilla sin decir nada.
- **`ModLoaderService`** / **`PaperService`** / **`PurpurService`** — instalan un mod loader (Fabric/Forge/NeoForge) o un
  build de Paper/Purpur. Purpur solo publica MD5 de sus builds, no SHA-256: HTTPS autentica el origen y el hash está
  para detectar una descarga corrupta, y así queda explicado en el propio servicio. También instalan
  un loader sobre un servidor existente, conservando el mundo. Limitación conocida: el endpoint meta de
  Fabric no publica checksums, así que su jar de servidor no se puede verificar por hash como las
  demás fuentes (Mojang SHA-1, Paper SHA-256…); en su lugar el jar descargado se valida
  estructuralmente (su `install.properties` debe coincidir con las versiones de juego/loader
  pedidas) y se descarta si no cuadra. Supuesto de confianza de Forge: su maven publica un `.sha1`
  junto a cada artefacto pero **desde el mismo servidor** (el ecosistema Forge no tiene firmas
  independientes), así que la verificación obligatoria del hash protege de corrupción, no de un
  servidor comprometido; como el instalador se *ejecuta*, además se valida estructuralmente (debe
  llevar `install_profile.json` o un manifest de installer) antes de que `java -jar` lo toque.
  NeoForge sigue el mismo esquema con un hash mejor: su maven publica un `.sha256` junto a cada
  artefacto, y eso es lo que se comprueba. El supuesto de confianza no cambia — mismo servidor que
  el jar — y sin hash no hay instalación, porque lo siguiente es un `java -jar`. Qué build
  corresponde a cada versión de Minecraft lo decide `NeoForgeVersions`, aparte de la descarga:
  NeoForge no tiene feed de promociones, así que la regla se deduce del número de build y se prueba
  por su cuenta.
- **`ModrinthService`** — busca en Modrinth y descarga mods/plugins (filtrados por el tipo y la
  versión del servidor), y gestiona el flujo de "buscar actualizaciones de mods".
- **`ModDependencyService`** — recorre las dependencias *obligatorias* de una versión, también las de sus
  dependencias, y dice cuáles faltan. Dos detalles de los datos de Modrinth lo condicionan: las dependencias no
  llevan **ningún rango de versiones** (o fijan un id de versión o nombran un proyecto), y por eso «ese proyecto
  ya está instalado» es una respuesta completa y no una aproximación; y `embedded` significa que la dependencia
  ya viene dentro del jar, así que instalarla otra vez produce el fallo de *mod duplicado* del cargador. El
  recorrido (`WalkAsync`) recibe la búsqueda como delegado, de modo que lo que decide se prueba contra una tabla
  y no contra Modrinth el día que se ejecuta la prueba.
- **`ContentManifest` / `ContentDependencyCheck`** — leen lo que cada jar declara de sí mismo (lo que ofrece y
  lo que necesita) y dicen qué falta. Tres formatos: `fabric.mod.json`, `plugin.yml` de Bukkit y el `mods.toml`
  de Forge/NeoForge, sin librería de YAML ni de TOML — solo hacen falta unas listas de nombres, y lo que no se
  entienda cuenta como «no declara nada». **Sin red, a propósito**: es la comprobación que corre al darle a
  Iniciar, y las llamadas de Modrinth que responderían a lo mismo se tragan los errores y devuelven vacío, así
  que sin conexión dirían que no falta nada justo donde equivocarse impide arrancar. Una prueba prohíbe que
  esos dos ficheros mencionen `HttpClient` o `ModrinthService`.
- **`NotificationCatalog` / `NotificationPalette`** — qué nivel y qué emoji le toca a cada tipo de aviso, y
  cuáles son los colores por defecto. Misma separación que `ServerTypeCatalog` y `ServerTypeBrushes`: los datos
  sin nada de UI aquí, las brochas de Avalonia en `NotificationBrushes`. Los colores que el usuario cambia
  viven en `NotificationSettings`, que se serializa a `settings.json`.
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
  encima en la esquina inferior derecha (con el nombre del servidor como título), solo cuando la app
  no tiene el foco; funcionan aunque el SO no soporte notificaciones.
- **`NotificationPreferences`** — decide qué notificaciones se muestran, combinando los ajustes
  globales (interruptor maestro + por tipo: entra, sale, muerte/baja, caída, reinicio agotado) con
  una posible anulación por servidor (`ServerConfig.UseCustomNotifications`). Las anulaciones por
  servidor se clonan campo a campo desde los ajustes globales para no compartir estado mutable.
  `DeathMessageDetector` detecta las líneas de muerte/baja en la consola para la notificación de
  muertes, exigiendo un nombre de jugador válido seguido inmediatamente por una frase de muerte
  vanilla conocida para reducir falsos positivos de chat o plugins.
- **`SecretProtector`** — cifra los secretos en reposo (DPAPI en Windows, AES-GCM + `.secret.key` en
  Linux/macOS), usado para la clave de agente por usuario de Playit (y la clave de escritura heredada). Si el cifrado falla, la clave **no** se
  persiste (nunca llega texto plano al disco): sigue funcionando durante la sesión, el fallo queda
  en el log diario y se avisa al usuario una vez.
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
La primera vez que el usuario conecta Playit, `MainViewModel.EnsurePlayitAgentAsync` muestra el
diálogo de código de configuración (abre `playit.gg/l/setup-third-party` solo al pulsar), canjea el
código pegado con `PlayitPartnerService.CreateAgentAsync` por una clave secreta de agente por
usuario y la guarda cifrada. Al crear un servidor (o con el botón "Crear túnel"), `MainViewModel`
llama a `PlayitApiService.EnsureMinecraftTunnelAsync` con esa clave. La dirección pública la detecta
periódicamente `ServerViewModel` con `GetAddressForPortAsync`, emparejando por puerto local.
Cumplimiento de las reglas de terceros de Playit: el navegador solo se abre al pulsar, un aviso
indica que la app no está afiliada a Playit y el usuario siempre puede acceder a su cuenta de Playit
directamente. Un agente autogestionado solo reenvía tráfico mientras su proceso corre, así que
`PlayitAgentRunner` descarga el binario oficial `playitd` de Playit (una vez, fijado a la versión
registrada) y lo ejecuta como proceso hijo oculto con `--secret <la clave por usuario>` mientras la
app está abierta y conectada — el usuario no instala nada. Como ese binario nativo es el código de
más privilegio que descarga la app, se **verifica contra un SHA-256 fijado en el código** (el de la
versión pinneada) antes de ejecutarse — al descargar y también al reutilizar una copia en caché — y
se borra/falla si no coincide, igual que el resto de descargas (`DownloadVerifier`). Un solo agente
sirve todos sus túneles. No disponible en macOS (Playit no publica binario de macOS); ahí el usuario
ejecuta Playit por su cuenta.

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

El mismo barrido responde a una segunda pregunta con los mismos hashes: qué **librerías faltan**.
`GetVersionsByHashAsync` dice qué *es* cada jar (id de proyecto y dependencias declaradas, al contrario que el
endpoint de actualizaciones, que dice qué podría sustituirlo), `ModDependencyService` calcula qué hace falta y
no está, y el panel ofrece instalarlo. Instalar un mod resuelve sus dependencias igual, en el mismo clic — que
es el arreglo del cargador de Fabric negándose a arrancar por un `fabric-api` que nadie pidió instalar.

## Localización

Todo el texto visible está en `Resources/Strings.resx` (español, idioma neutral/base) más los
archivos satélite `Strings.en.resx`, `Strings.pt.resx`, `Strings.fr.resx`, `Strings.de.resx`. El
código los lee con `Localizer.Get("Clave")` (y `string.Format` para parámetros); el XAML usa la
extensión de marcado `{loc:Loc Clave}`. El idioma activo viene de `AppSettings.Language` y se aplica
en `App.OnFrameworkInitializationCompleted` antes de crear ninguna ventana, por eso cambiar de idioma
requiere reiniciar.
Mira [Cómo contribuir](contributing.es.md) para añadir un idioma o un texto nuevo.
