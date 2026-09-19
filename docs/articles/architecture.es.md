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
| `Models/` | Datos puros: configuración persistida (`ServerConfig`), ajustes (`AppSettings`), enums (`ServerState`, `PlayitState`). Dos subcarpetas guardan las formas que vienen de fuera: `Modrinth/` (lo que devuelve la API) y `Store/` (lo que enseña la tienda, independientemente de su origen). |
| `Services/` | Toda la lógica sin interfaz: procesos, archivos, red, Java, Playit, puertos, etc. Cada servicio es una clase pequeña y centrada. |
| `ViewModels/` | El estado y los comandos a los que se enlaza la interfaz (`MainViewModel`, `ServerViewModel`, y uno por panel: `ServerModsViewModel`, `ServerBackupsViewModel`, `ModDetailsViewModel`). Estado enlazable y `RelayCommand`s, no controles de Avalonia. |
| `Views/` | Las ventanas/diálogos `.axaml` (XAML de Avalonia) y su code-behind ligero. |
| `Localization/` | El sistema de traducción (`Localizer` + la extensión de marcado `{loc:Loc}`). |
| `Behaviors/` | Comportamientos adjuntos: `AutoScrollBehavior` (la consola sigue la última línea), `ResetScrollBehavior` (una lista vuelve arriba cuando su contenido se *sustituye*, no cuando se añade), el color del MOTD en `MinecraftMotd` y el Markdown en `MarkdownBody`. |
| `Controls/` | Controles propios (`Sparkline` para las mini-gráficas de CPU/RAM). |
| `Styles/` | `Shared.axaml`: los estilos que necesita más de una vista (`Border.card`, `Border.tile`, el texto de las estadísticas…), incluidos desde `App.axaml`. |
| `Resources/` | `Strings*.resx` (traducciones), `app.ico` y los dos archivos de datos de la tienda (`store-tags.json`, `store-summaries.json`). |

> **Los conversores de valores viven en `ViewModels/`** — no existe una carpeta `Converters/`. Son
> cinco: `BoolOpacityConverter`, `HexBrushConverter` (de un hex de los ajustes a un brush),
> `ConsoleBrushConverter` y `ConsoleHighlightConverter` (el color de una línea de consola y el
> resaltado de lo buscado) y `NoticeBrushConverter` (el fondo del aviso de instalación).
>
> La mitad sin interfaz de cada decisión de color se deja fuera de `ViewModels/` a propósito, para
> que un ajuste que se serializa a `settings.json` no tenga que saber que Avalonia existe:
> `ConsoleColors` / `ConsolePalette`, `NotificationPalette` / `NotificationBrushes` y
> `ServerTypeCatalog` / `ServerTypeBrushes` son tres casos del mismo reparto — los hex en
> `Services/`, los brushes en `ViewModels/`.

> **`ServerConfig` avisa de sus cambios, y los view models comparten la instancia.** Él y
> `NotificationSettings` son los dos tipos de `Models/` que lo hacen: a los dos los edita un diálogo
> en el sitio mientras otra cosa los está enseñando. Eran objetos planos, con el argumento de que un
> modelo que se guarda no debería depender de MVVM; ese argumento no sobrevive a mirar que `Models/`
> y `ViewModels/` son carpetas de un mismo ensamblado, así que la dependencia ya estaba, y lo que la
> regla compraba de verdad era una familia de fallos. Nada de lo derivado de la config se recalculaba
> nunca: convertir un servidor cambiaba el disco y dejaba a la app describiendo lo que la carpeta
> había dejado de ser, hasta reiniciarla.
>
> **Esto no cambia `servers.json`.** System.Text.Json por reflexión escribe propiedades públicas;
> las generadas conservan exactamente los nombres que tenían las auto-propiedades; `ObservableObject`
> solo aporta eventos, que no se serializan. El *orden* de las claves no se promete, y nadie lee el
> fichero por posición. `ServerConfigFormatTests` sujeta cada parte de eso: el conjunto exacto de
> claves, que `Type` siga siendo el entero que es el formato, que las dos rutas calculadas con
> `[JsonIgnore]` sigan fuera, y que un fichero escrito por una versión anterior siga abriéndose.
>
> No le pongas nunca a ninguna de las dos un `Clone` hecho con `MemberwiseClone`: copia el delegado
> `PropertyChanged`, así que la copia lanza cambios a los suscriptores del original.
> `NotificationSettings.Clone` es campo a campo, y `AppSettings` —que sí usa `MemberwiseClone`— se
> deja a propósito como objeto plano, porque el diálogo de ajustes edita una copia y la vuelca al
> aceptar.

> **Una sola tabla dice qué alimenta cada campo de la config.** `ServerConfigEffects` tiene una fila
> por propiedad de `ServerConfig`: qué propiedades de `ServerViewModel` y `ServerModsViewModel` hay
> que anunciar, y qué hay que rehacer que una notificación no sabe expresar —releer el puerto,
> reescanear la carpeta de contenido, cerrar la ficha de la tienda, volver a buscar, reabrir el
> listener de despertar, recargar los backups—. `ServerViewModel` se suscribe una vez a
> `Config.PropertyChanged` y aplica la fila, en el hilo de interfaz (el crossplay escribe el puerto
> Bedrock desde una continuación en segundo plano). Un campo que no enseña nadie también tiene fila,
> con el motivo escrito.
>
> **`ServerConfigEffectsTests` es lo que sujeta esto.** Toda propiedad escribible de `ServerConfig`
> tiene que aparecer exactamente una vez, y toda propiedad de view model que nombre una fila tiene
> que seguir existiendo. Un campo añadido sin fila falla el día que se escribe, y un renombrado que
> se olvide de la tabla falla en vez de anunciar un nombre que no escucha nadie. Ese es todo el
> asunto: el fallo nunca fue difícil, solo era silencioso.
>
> Persistir no es a propósito uno de los efectos. El diálogo de editar escribe en la config viva
> según se teclea, así que guardar en cada cambio reescribiría `servers.json` en cada tecla; guardar
> se queda donde está, una vez, cuando se acepta un diálogo.
>
> Por lo mismo, la caja de la carpeta en `AddEditServerDialog` es el único enlace con
> `UpdateSourceTrigger=LostFocus`. La carpeta es la identidad entera del servidor en disco, y
> volcarla en cada tecla releería el puerto, el MOTD, el icono, la carpeta de contenido y la lista de
> backups una vez por letra, contra rutas que todavía no existen. Las demás cajas de ahí vuelcan
> según se escribe, que es lo que hace que la tarjeta se actualice mientras la editas.

> **Un constructor monta; `Activate()` arranca.** `ServerViewModel` y `MainViewModel` se parten en
> dos. El constructor lee —la config, la paleta de consola, los archivos del propio servidor— y no
> deja nada en marcha detrás. `Activate()` es todo lo que sale del objeto: los timers de sondeo, las
> suscripciones al `PlayitManager` / `PlayitAgentRunner` compartidos, la consulta de los túneles, el
> socket de despertar bajo demanda, la comprobación de actualizaciones. `MainWindow` llama a
> `MainViewModel.Activate()` desde `Loaded`, y eso llega a todos los servidores; uno registrado más
> tarde lo activa `Register` en el momento.
>
> `ShutdownAsync()` es el espejo exacto, y los dos se pueden llamar dos veces sin daño — `Loaded`
> vuelve a dispararse cada vez que la ventana regresa de la bandeja. Por eso se pueden construir en
> una prueba: antes del corte, construir uno arrancaba tres timers, abría un socket y llamaba a la
> red, así que nada que los tocara se podía probar salvo por sus piezas puras.

Los datos se guardan **por usuario** en `%APPDATA%\McServerLauncher\`
(`~/.config/McServerLauncher/` en Linux y macOS):

- `servers.json` — la lista de servidores y la configuración de cada uno.
- `settings.json` — ajustes globales (idioma, clave secreta del agente de Playit, última versión vista…).
  Ambos JSON se escriben de forma **atómica** (`AtomicJsonFile`): la versión anterior se conserva
  como `.bak`, y un archivo corrupto se aparta como `.bad` y se recupera desde el `.bak` cuando es
  posible (avisando al usuario al arrancar en vez de perder la lista en silencio).
- `java\` — las versiones de Java que instala la app (Temurin/Adoptium).
- `logs\` — el log de consola persistente (`launcher-yyyy-MM-dd.log`, se poda a los 14 días).
- `cache\images\` y `cache\store\` — las cachés en disco de la tienda: iconos y capturas de la
  galería (`ImageCache`, se podan a los 30 días) y las respuestas de la API (`StoreCache`). Ambas son
  prescindibles; borrarlas cuesta unas cuantas peticiones y nada más.
- `playit-agent\` — el binario oficial `playitd` de Playit, descargado una vez y fijado a la versión
  que registra la app (`PlayitAgentRunner`).
- `instance.lock` — el bloqueo exclusivo de archivo que mantiene la app en una sola copia por usuario
  (`SingleInstance`).
- `.secret.key` — la clave AES-GCM que cifra los secretos en Linux/macOS (Windows usa DPAPI, así que
  ahí no hay archivo de clave).
- *(opcional)* `store-tags.json` / `store-summaries.json` — si existe cualquiera de los dos,
  sustituye a la copia embebida en la app, así que las etiquetas y los resúmenes en lenguaje llano se
  pueden cambiar sin compilar nada.

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
  `PortService`, todo orquestado por `CreateServerDialog`.) La semilla que se escriba ahí, si se escribe, va
  como `level-seed`, escapada como valor de un properties de Java —la barra invertida duplicada, lo que no es
  ASCII como `\uXXXX`— y **solo se pide al crear**: convertir el tipo o cambiar la versión conserva el
  mundo, así que no queda semilla que elegir.
- **`WorldSeed`**, **`NbtReader`**, **`SeedMapLink`** — la semilla que enseña el diálogo de configuración.
  `level-seed` no lo es: se lee una vez, al generar, y vacío significa aleatoria. La de verdad la guarda el
  mundo, y la ha guardado en tres sitios: `data/minecraft/world_gen_settings.dat` → `data.seed` desde la
  26.1 (un mundo real de la 26.2 no la tenía en `level.dat`), `level.dat` → `Data.WorldGenSettings.seed`
  desde la 1.16, y `Data.RandomSeed` antes. `NbtReader` lee ese único número del NBT comprimido y no lanza
  nunca: una partida dañada o a medio escribir responde «no está», con longitudes y anidamiento acotados.
  Como Minecraft ya la cambió de sitio una vez, la respuesta del servidor al comando `seed` se recuerda en
  `ServerConfig.LastKnownSeed` como respaldo. `SeedMapLink` abre Chunkbase en la versión que cubre la del
  servidor, con una tabla copiada de la web; una versión más nueva que la tabla, o una snapshot, va sin
  `platform` y la web elige la suya más reciente.
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
  entienda cuenta como «no declara nada». **Los jars dentro de jars cuentan**: los dos cargadores dejan que
  un mod lleve otros dentro (`META-INF/jars/`, `META-INF/jarjar/`), y `fabric-api` son unos cuarenta módulos
  así, de modo que lo que ofrece un jar anidado se suma al de fuera —siguiendo unos cuantos niveles— y lo que
  necesita no, porque un hueco dentro de un paquete es cosa del cargador. El lector de dependencias de la
  pestaña de Mods pasa por el mismo código en vez de por una copia suya; antes había dos, y solo uno lo sabía.
  **Sin red, a propósito**: es la comprobación que corre al darle a
  Iniciar, y las llamadas de Modrinth que responderían a lo mismo se tragan los errores y devuelven vacío, así
  que sin conexión dirían que no falta nada justo donde equivocarse impide arrancar. Una prueba prohíbe que
  esos dos ficheros mencionen `HttpClient` o `ModrinthService`.
- **`NotificationCatalog` / `NotificationPalette`** — qué nivel y qué emoji le toca a cada tipo de aviso, y
  cuáles son los colores por defecto. Misma separación que `ServerTypeCatalog` y `ServerTypeBrushes`: los datos
  sin nada de UI aquí, las brochas de Avalonia en `NotificationBrushes`. Los colores que el usuario cambia
  viven en `NotificationSettings`, que se serializa a `settings.json`.
- **`ConsoleLineClassifier` / `ConsoleColors`** — de qué va cada línea de consola y de qué color se pinta. El
  origen manda sobre el texto: los mensajes de la app se etiquetan donde se emiten, porque su texto está
  traducido —los prefijos `[Launcher]`, `[Error]` y `[Players]` viven *dentro* de los valores del resx— y un
  clasificador basado en ellos funcionaría en español y dejaría de funcionar en alemán. `stderr` viene marcado
  desde `ServerProcessManager`, que antes lo mezclaba con la salida normal en el mismo manejador, y va en rojo
  salvo las líneas `WARNING:` de la propia JVM, cuyo formato fija Java. Solo `stdout` se lee: el corchete de
  vanilla (nivel en el **segundo**, no en el primero) y el de Paper. **Una línea sin prefijo de log pertenece a
  la entrada de encima** —la lista de mods de Fabric, las líneas bajo un aviso de varias líneas, el mensaje de
  una excepción—, así que toma el nivel de esa entrada en vez de juzgarse sola; el view model le pasa el tipo
  de la línea anterior de stdout. Las trazas se reconocen por su forma (`at x.y(File:1)`, `Caused by:`,
  `... 12 more`), nunca por la sangría: el cargador sangra su lista de mods con tabuladores, y un tabulador
  pintaba de rojo todos los mods.
- **`BlueMapConsent`** y `ServerProcessManager.ImpliedJvmFlags` — los dos avisos de arranque que la app
  resuelve ella misma, porque le tocan a ella. Desde Java 22 añade `--enable-native-access=ALL-UNNAMED` a la
  línea de comandos que construye: los mods que usan JNA provocan un aviso que dice que un Java futuro
  *bloqueará* la llamada, y una opción desconocida impediría arrancar a una JVM antigua, de ahí el corte por
  versión. `--sun-misc-unsafe-memory-access` no se añade a propósito: sus valores aceptados cambian de una
  versión a otra. Y cuando BlueMap avisa de que su descarga no está aceptada, la app **pregunta** —es aceptar
  bajar ficheros del cliente de Mojang— y si dices que sí cambia ese único valor en `core.conf` y manda
  `bluemap reload`. El resto de avisos de un arranque con mods (refmaps, mixins dirigidos a mods ausentes, un
  valor del registro de Windows, Distant Horizons recomendando ZGC para los FPS del cliente) no le toca
  cambiarlos a la app.
- **`ServerDetectionService`** — inspecciona una carpeta para averiguar el tipo/versión de un servidor
  existente cuando el usuario añade uno que ya está. Corre dos veces: al entrar desde *Añadir
  servidor*, y otra vez al arrancar para los servidores guardados antes de que esos campos
  existieran. No rellena nada en una config que ya dice su versión, así que la segunda pasada sale
  gratis y ninguna de las dos puede llevarle la contraria al usuario.
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
- **`UpdateService`** / **`SelfUpdater`** — `UpdateService` pregunta a GitHub si hay una release más
  nueva y elige el asset para *esta* plataforma y arquitectura (el instalador de Windows, el AppImage
  de Linux, el `.dmg` de macOS); `SelfUpdater` es quien lo aplica. Lee la **lista** de releases, no
  `/releases/latest`, porque GitHub deja las pre-releases fuera de esa última y una beta publicada
  así sería invisible para la app. La verificación contra el asset `SHA256SUMS.txt` de la release es
  **obligatoria**: si el checksum falta o no se puede leer, la actualización in situ se rechaza y se
  abre la página de la release en su lugar.

### Servicios de apoyo

- **`AppSettingsService`** / **`ServerStorageService`** — los dos dueños del JSON de la app:
  `settings.json` y `servers.json`. Ambos pasan por `AtomicJsonFile` y ambos informan de qué pasó al
  cargar, para que un archivo corrupto salga a la superficie al arrancar en vez de convertirse en una
  lista de servidores vacía. Los dos aceptan una carpeta de datos opcional, y `MainViewModel` acepta
  otra y se la pasa a ambos: sin eso, una prueba que se acerque a cualquiera de los dos archivos lee
  y reescribe el de verdad de quien la esté ejecutando.
- **`AtomicDownload`** / **`AtomicTextFile`** — la misma garantía para los otros dos tipos de
  escritura. Una descarga aterriza en `<destino>.part` y se verifica ahí, así que una interrumpida no
  puede sustituir un archivo que funcionaba por la mitad de otro; un archivo de configuración que es
  de la app se escribe solo cuando de verdad ha cambiado, y nunca a medias.
- **`SingleInstance`** — una sola copia en marcha por usuario, y un segundo lanzamiento trae la
  primera al frente. Es una garantía de corrección, no una cuestión de orden: dos copias arrancan
  cada una sus procesos de servidor y sus escuchas de wake, y dos JVM sobre la misma carpeta de mundo
  es como se corrompen los mundos. Un archivo bloqueado responde a "¿hay alguien más?" (el sistema lo
  suelta incluso si matas el proceso, al contrario que un PID file) y una named pipe lleva el aviso
  de "ponte delante".
- **`ServerNameRule`** / **`BukkitPathRule`** — el nombre de la carpeta de un servidor, comprobado
  antes de que sea un servidor que no arranca: lo que Windows prohíbe de entrada (caracteres
  ilegales, nombres reservados de dispositivo, un punto o un espacio al final) y, aparte, los dos
  caracteres desde los que Paper y Purpur se niegan a ejecutarse — incluso cuando están en una
  carpeta *por encima* de la del servidor, que no es nuestra para renombrarla.
- **`LoaderPaths`** — dónde deja cada cargador los archivos que hay que volver a encontrar (el
  directorio de versión y el archivo de argumentos con el que se lanzan Forge y NeoForge). En un solo
  sitio porque lo necesitan el instalador, el lanzador y el detector, y un cargador que falte en uno
  de los tres se instala perfectamente y luego no arranca.
- **`VerifiedJarDownload`** — anunciar el tamaño, descargar de forma atómica, verificar y avisar de
  que está. Compartido por Paper y Purpur, que solo se diferencian en el algoritmo de hash que
  publican.
- **`FileHashCache`** — el SHA-1 de un archivo, recordado mientras el archivo no cambie (la clave es
  ruta + tamaño + fecha de escritura). Si no, la pestaña de mods hashea cada jar dos veces por clic:
  una para buscar actualizaciones y otra para ver qué hay ya instalado.
- **`ContentMigrationService`** — qué pasa con el contenido instalado cuando un servidor cambia de
  familia: la carpeta `mods/` o `plugins/` anterior se aparta en vez de dejarla para que la cargue
  algo que no sabe leerla.
- **`MultiVersionService`** — ViaVersion *y* ViaBackwards, que no son intercambiables: el primero
  admite clientes más nuevos que el servidor y el segundo más antiguos, e instalar solo uno parece
  que la función está rota para la mitad de quienes la prueban. Solo servidores de plugins, y
  deliberadamente independiente del crossplay.
- **`DesktopShortcutService`** — el botón "Añadir al escritorio". Tres cosas distintas según la
  plataforma (un `.lnk`, una entrada `.desktop` que tiene que ser ejecutable y estar marcada como de
  confianza en GNOME, un enlace simbólico al bundle), todas apuntando a desde dónde se está
  ejecutando de verdad esta copia y no a una ruta de instalación supuesta.
- **`WindowBehavior`** — qué hacen minimizar y cerrar, según los ajustes. Estado global aplicado sin
  reiniciar, con la misma forma que `NotificationPreferences.Global` y `ConsolePreferences`.
- **`BrowserLauncher`** — la única manera de abrir un enlace. Solo pasan URLs http(s) absolutas,
  porque en la tienda la URL la escribe el autor de un mod, no nosotros.
- **`MarkdownParser`** / **`MarkdownBody`** — un lector de Markdown deliberadamente parcial para las
  descripciones largas de Modrinth, y el comportamiento que convierte sus bloques en controles.
- **`MinecraftRange`** — si una versión de Minecraft cumple el rango que declara un mod.

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
periódicamente `ServerViewModel` con `GetTunnelAsync`, emparejando por puerto local **y
protocolo**: un servidor con crossplay tiene dos túneles, así que toda búsqueda pasa por
`PlayitApiService.Match`, la única definición de «el mismo túnel». La lista de túneles se comparte
entre servidores tras una caché de 25 segundos para que N servidores no hagan N llamadas; la ráfaga
corta de consultas posterior a crear un túnel pasa `fresh: true` para saltársela, porque playit tarda
unos segundos en publicar la dirección y si no la primera respuesta vacía se les serviría a los
reintentos; crear un túnel lanza esa misma ráfaga en el lado de Java, que antes esperaba al
temporizador de 30 segundos. La caja de la dirección es de solo lectura — enseña lo que Playit ha
asignado, no una preferencia — y la línea de debajo es `TunnelAddressState` (`Waiting` / `NoTunnel` /
`Ready` / `Failed`), la hermana de `BedrockAddressState`: tres situaciones distintas se dibujaban
como una sola caja vacía.
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
Al arrancar, `MainViewModel.CheckForUpdatesAsync` pide a `UpdateService` la release más nueva y el
asset de esta plataforma. El botón **Actualizar** (`UpdateNowCommand`) lo descarga, lo verifica
contra el `SHA256SUMS.txt` de la release, detiene los servidores y se lo pasa a `SelfUpdater`.

**Todas las plataformas se actualizan solas; lo que cambia es el mecanismo.** Windows ejecuta el
instalador en silencio, Linux sustituye el AppImage desde el que está corriendo la app y macOS monta
el `.dmg` y reemplaza el bundle `.app`. Lo que comparten es la forma: no se toca nada hasta que hay
en disco un paquete completo y verificado por checksum, y cualquier fallo deja la instalación actual
funcionando. Hay instalaciones que no pueden sustituirse a sí mismas — un AppImage que root movió a
`/opt`, un bundle en una ubicación de solo lectura, la app arrancada con `dotnet run` — y
`SelfUpdater.Blocker` lo dice; esas, y una release que no traiga nada para esta plataforma, caen en
abrir la página de la release.

Tras actualizar,
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

### Jugar desde Bedrock (crossplay)
Una casilla, y tres cosas que tienen que encajar — que es justo por lo que hacerlo a mano sale mal.
`CrossplayService.InstallAsync` instala **Geyser** en el servidor (desde Modrinth, por la misma ruta
de instalación verificada que cualquier otro mod o plugin) para que entienda a los clientes de
Bedrock, y **Floodgate** para que esos jugadores no tengan que tener cada uno Minecraft: Java.
Floodgate se parte según el origen: Modrinth lleva las builds de Fabric y NeoForge, y solo la web de
descargas de GeyserMC (`GeyserDownloadsApi`) lleva la de Spigot que necesitan Paper y Purpur.

Después, el **segundo túnel**: Java es TCP y Bedrock es UDP, y uno no puede llevar al otro, así que
`MainViewModel` crea un túnel UDP junto al de Java. `CrossplayService.PickBedrockPort` elige el
puerto local (19132 es solo un punto de partida — se ocupa en cuanto hay dos servidores), evitando
tanto los puertos de otros servidores registrados como los que ya tiene la cuenta de Playit del
usuario.

Por último, `GeyserConfigService` escribe lo que Geyser no puede deducir solo: `auth-type` (un
servidor con Floodgate que se queda en `online` rechaza a todos los jugadores de Bedrock), el puerto
UDP local y **`broadcast-port`** — detrás de un túnel, el puerto al que se conecta la gente es el
público del túnel, y el launcher es el único componente que conoce los dos números porque es quien
creó el túnel. `RepairConfig` vuelve a aplicarlo cuando una reinstalación resetea el archivo.

Lo bien que funcione todo esto es una propiedad del tipo de servidor, no una promesa:
`ServerTypeCatalog` lleva un `CrossplayLevel` de tres valores y los dos diálogos enseñan la
advertencia antes de marcar la casilla. En Fabric, otra casilla instala **Hydraulic**
(`HydraulicService`) para que los bloques y objetos que añaden los mods se conviertan para los
clientes de Bedrock; es solo para Fabric porque Hydraulic dejó de publicar builds de NeoForge en
febrero de 2026. El único fallo que no se puede evitar — un servidor NeoForge rechazando la conexión
sin mods de Geyser — al menos se reconoce en la consola y se explica en el idioma del usuario con
`CrossplayDiagnostics`.

### Dormirse vacío y despertar cuando alguien entra
Dos mitades, las dos por servidor y las dos desactivadas por defecto.

**Dormirse** es `ServerViewModel.CheckIdleShutdown`, ejecutado en el mismo tick que refresca la lista
de jugadores: en cuanto un servidor lleva `ServerConfig.IdleShutdownMinutes` sin nadie dentro, se
detiene solo, anunciándolo en la consola y como notificación. La ventana enseña una cuenta atrás en
vivo (`IdleCountdownText`), y un servidor recién despertado tiene un periodo de gracia para que nunca
se le pare antes de que a nadie le dé tiempo a entrar.

**Despertar** es `WakeOnDemandListener`, que ocupa el puerto del servidor mientras este está parado y
habla la parte pequeña y estable del protocolo de Minecraft necesaria para ser honesto al respecto:
el handshake, el estado de la lista de servidores (para que la lista muestre *"Apagado · entra para
encenderlo"* con su icono y su límite de jugadores reales) y el disconnect de login (para que quien
pulse Entrar reciba un mensaje mientras arranca). **Lo que lo despierta es pulsar Entrar**, no que le
hagan ping — el cliente repite el ping cada pocos segundos mientras la pantalla de multijugador está
abierta, así que despertar con una petición de estado arrancaría el servidor una y otra vez para
gente que ni siquiera está jugando. Con un túnel de Playit este socket es accesible desde internet,
así que todo lo que lee se trata como hostil: longitudes acotadas, una fecha límite por conexión y un
tope de cuántas hay a la vez.

### La tienda: búsqueda, etiquetas y lenguaje llano
`ServerModsViewModel` pide a `ModrinthService` resultados ya filtrados por el cargador y la versión
del servidor — un resultado que el servidor no puede ejecutar es peor que ninguno, porque se instala
y luego el servidor no levanta. Cada resultado se convierte en un `StoreItem`, la forma independiente
del origen con la que trabaja el resto de la tienda, y entonces se le añaden dos cosas que Modrinth
no da:

- **`StoreTagService`** lo convierte en las etiquetas propias de la app. Las categorías de Modrinth
  son gruesas (casi la mitad de los mods de servidor más usados están en "utility") y no responden a
  lo que más le importa a quien lleva un servidor — si los jugadores tienen que instalarlo también —
  así que las categorías, unas reglas por palabras clave y el lado cliente/servidor se combinan a
  través de `Resources/store-tags.json`.
- **`StoreSummaryService`** responde a "¿qué le hace esto a mi servidor?" en el idioma del usuario,
  desde un catálogo escrito a mano (`Resources/store-summaries.json`, generado por
  `tools/generate-store-summaries.py`) con una frase de reserva construida con lo que Modrinth sí
  dice. Es una consulta local: sin petición, sin clave, y funciona sin conexión.

Los dos archivos se pueden sustituir por una copia en la carpeta de datos del usuario, así que las
etiquetas y los resúmenes se cambian sin compilar. Abrir un resultado muestra `ModDetailsViewModel` —
galería, versiones, dependencias, enlaces y proyectos relacionados — pintado en dos pasadas, para que
lo que ya traía el resultado de búsqueda aparezca al instante y el resto llegue cuando vuelvan sus
peticiones. `StoreCache` (memoria, luego disco, luego red) e `ImageCache` son lo que hace que volver
atrás y abrir algo otra vez no cueste nada, y lo que hace que un proyecto ya visto se abra sin
conexión.

### Cambiar el tipo de un servidor
`InstallLoaderDialog` convierte un servidor existente en el sitio, **conservando el mundo**: de
Vanilla a un cargador o a un servidor de plugins, de un cargador a otro, o de cualquiera de ellos de
vuelta a Vanilla. Ofrece la misma lista que el diálogo de creación (el `ServerTypePicker`
compartido) e instala por el mismo `ServerJarInstaller`, así que los dos ya no pueden separarse — se
separaron, y un tipo presente en uno y ausente en el otro producía en silencio un servidor Vanilla.
La advertencia sobre el botón depende de la *dirección* del cambio, porque las direcciones no son
igual de seguras: ganar un cargador es aditivo, y bajar a Vanilla o cruzar de familia no lo es. El
contenido que la familia nueva no sabe leer lo aparta `ContentMigrationService` en vez de dejar que
falle al cargar.

La conversión escribe en el `ServerConfig` que la app ya está mostrando, y **nadie pide después
ningún refresco**. La config avisa de cada campo que ha cambiado, `ServerConfigEffects` dice lo que
ese campo cuesta, y la insignia, la versión, la pestaña de Mods, sus fichas de filtro, la lista de
instalados —la carpeta de la familia vieja acaba de apartarse— y la búsqueda de la tienda van
solos. Incluso cancelar el diálogo después queda cubierto, porque restaurar la copia anuncia sus
propias asignaciones.

Antes esto reconstruía el view model entero, y solo cuando había cambiado el *tipo* y el servidor
estaba parado. Convertir Fabric 1.21.1 a Fabric 1.21.4 no cambiaba entonces nada en pantalla, y el
navegador seguía ofreciendo mods elegidos para una versión que el servidor ya no ejecutaba — que es
lo que hacía fallar una instalación minutos después, lejos de la conversión que lo causó. Un
refresco general al cerrar el diálogo fue el primer arreglo; también se ha ido, porque dejarlo
significaría que la app nunca ejerce el mecanismo que lo sustituyó.

### Darle la lista de mods a tus jugadores
`ServerModsViewModel.ExportModpack` es el selector de archivos y nada más; `BuildModpackAsync` recibe
una ruta, así que la parte que merece pruebas corre sin ventana. `ModpackWriter` monta el zip entrada
a entrada, leyendo cada jar de donde ya está —antes montaba una copia del paquete entero bajo
`%TEMP%`— y escribiendo cada archivo de texto con sus propios finales de línea. El paquete lleva los
jars y un archivo de instrucciones traducido que nombra el servidor, su tipo y su versión de
Minecraft, y los pasos para ese tipo. Es la respuesta a "¿qué les mando a mis amigos para que puedan
entrar?", que si no significa explicar la instalación de un cargador por chat.

**Lo que el paquete deja fuera.** Un servidor con mods suele llevar jars que al jugador no le sirven
de nada —Geyser y Floodgate para el crossplay de Bedrock, un plugin de permisos, un mod de copias de
seguridad—, megabytes que descarga y copia a su carpeta de mods para nada. `ExportSelection` lo
decide con dos fuentes que por separado no bastan: lo que declara el jar
(`ContentManifest.ContentSide`, leído del `environment` de `fabric.mod.json` o del `side` de
`mods.toml`) y el `client_side` de Modrinth. La regla es asimétrica y cabe en una frase: **un jar se
queda fuera cuando alguna de las dos fuentes dice que es solo de servidor y ninguna dice que el
cliente lo necesita.** Un jar que se declara de cliente no se cae nunca. Declararse de *ambos* lados
no cuenta para nada, y averiguarlo costó un modpack de verdad: la primera carpeta a la que se apuntó
tenía once mods y los once escribían `environment: "*"`, Floodgate incluido — un autenticador de
Bedrock que no hace nada en un cliente. Es lo que escribe la plantilla, así que leerlo como una
afirmación del autor dejaba la función incapaz de excluir nada. Por encima de la tabla hay tres
reglas: unos metadatos que digan que el proyecto no funciona en ningún lado se
ignoran por rotos, lo que necesite un jar que se queda vuelve a entrar de forma transitiva, y un
paquete nunca se vacía. La tienda tiene cuatro segundos y después el paquete se monta solo con los
jars, que excluye estrictamente menos; el aviso lo dice. Nada de `ExportSelection` toca la red, y hay
una prueba que lo comprueba contra el archivo.

Al terminar, un cartel descartable bajo la lista de instalados nombra lo que se ha quedado fuera, con
un botón que rehace el mismo zip incluyéndolo todo. A propósito después y no un diálogo antes: un
diálogo le cobra un clic al caso normal para servir al raro, y las instrucciones dentro del paquete
llevan la misma nota, porque el paquete tiene que explicarse a quien lo recibe. El botón no aparece
en servidores de plugins, donde el paquete es un zip que el jugador no puede usar para nada.

**Los scripts del paquete.** Junto a los jars van `install-mods-windows.bat` e
`install-mods-unix.sh`, para que recibir un paquete no sean cuatro pasos a mano cuyo tercero es
donde se borran los mods de alguien. `InstallScriptBuilder` rellena una plantilla guardada como
`EmbeddedResource` (`Resources/scripts/*.in`) con los mensajes de los `.resx`: la forma es código y
el texto está traducido, así que cada frase hereda las comprobaciones de paridad y la lógica no
hereda ninguna. Cada marcador se sustituye por un mensaje entero y terminado —nunca por una cadena
de formato—, así que ninguna traducción puede colar un metacarácter de shell, y el escapado de
`ShellSafe`/`BatchSafe` lo respalda.

Los scripts **apartan y no borran nunca nada del jugador** —lo único que eliminan es el instalador
del cargador que ellos mismos se han descargado, ver más abajo—. Los jars que ya estaban van a una carpeta
`mods-backup-<marca>` hermana de la de mods (hermana, para que el cargador no la reescanee), un
movimiento fallido para en seco en vez de dejar media instalación, y la marca de tiempo se calcula al
exportar porque `%DATE%` en un `.bat` sale con el formato local — que en media Europa mete barras
dentro de un nombre de carpeta. **Listan las carpetas que existen de verdad** y dejan elegir:
encontrar `.minecraft` demuestra que el launcher oficial se instaló alguna vez, nunca que sea el que
se va a usar, así que cada instancia de Prism, MultiMC, CurseForge o Modrinth App que aparezca se
ofrece con su propio nombre. El `.sh` dibuja esa lista como un menú que se recorre con las flechas;
batch no sabe leer teclas de dirección —eso pide PowerShell, que los proveedores de correo bloquean
igual y que choca con la política de ejecución—, así que el `.bat` usa `choice.exe`: una sola tecla,
sin Enter, con todas las opciones a la vista. Los dos colorean su salida, y el `.bat` solo en
consolas lo bastante nuevas como para interpretar los códigos en vez de imprimirlos. Cada `choice`
lleva tiempo de espera y valor por defecto, porque batch no tiene forma de preguntar si hay alguien
escuchando y si no se quedaría esperando una tecla que no puede llegar. `MCSL_MODS_DIR` se salta
todo, y el `.sh` no pregunta nunca sin terminal — que es lo que evita que se cuelgue con una tubería,
y lo que lo hace probable.

**Instalar el cargador.** Cuando falta el perfil, el script ofrece instalarlo en vez de limitarse a
nombrar una web. `ClientLoaderInstall` resuelve al exportar el instalador y su hash publicado desde
el maven del propio cargador, así que el script no tiene ninguna versión que averiguar: Fabric por su
CLI `client`, Forge y NeoForge por `--installClient`. Busca Java en el `PATH` y, si no está, dentro
de la carpeta `runtime` del propio Minecraft, que es donde el lanzador oficial guarda un JRE que la
mayoría de jugadores no sabe que tiene. **La descarga se verifica contra ese hash antes de pasársela
a Java**, y se borra si no coincide — lo único que estos scripts pueden borrar, y hay una prueba que
enuncia la regla con esa precisión. Si el instalador no se puede resolver al montar el paquete,
sencillamente no va, y el script vuelve a limitarse a avisar: un paquete peor, no uno roto. `InstallScriptSmokeTests` ejecuta el script de verdad sobre un
`old.jar` plantado y comprueba que sigue existiendo después. El bit de ejecución se pone, pero nada
depende de él: la invocación documentada es `bash install-mods-unix.sh`, que no lo necesita y además
esquiva la cuarentena de macOS.

### Una sola copia en marcha
`Program.Main` reclama `SingleInstance` antes que nada. Si otra copia ya tiene el bloqueo, esta le
avisa por la named pipe para que se ponga delante y sale sin llegar a crear una ventana. Eso es una
garantía de corrección, no orden: una segunda copia arranca sus propios procesos de servidor,
escuchas de wake y temporizadores de inactividad, muestra como parado un servidor que la primera
tiene corriendo, y pulsar Iniciar entonces significa dos JVM escribiendo sobre la misma carpeta de
mundo.

### Buscar actualizaciones de mods/plugins
`ServerModsViewModel` pide a `ModrinthService` identificar cada archivo instalado en Modrinth y marcar
los que tienen una versión más nueva; el usuario actualiza cada uno con un clic (descarga verificada
con checksum vía `DownloadVerifier`, conservando su estado activado/desactivado).

El mismo barrido responde a una segunda pregunta con los mismos hashes: qué **librerías faltan**.
`GetVersionsByHashAsync` dice qué *es* cada jar (id de proyecto y dependencias declaradas, al contrario que el
endpoint de actualizaciones, que dice qué podría sustituirlo), `ModDependencyService` calcula qué hace falta y
no está, y el panel ofrece instalarlo. Instalar un mod resuelve sus dependencias igual, en el mismo clic — que
es el arreglo del cargador de Fabric negándose a arrancar por un `fabric-api` que nadie pidió instalar.

Tres detalles que escondieron un fallo mucho tiempo cada uno, y cada uno tiene ya su prueba:

- **Los hashes van a Modrinth en minúsculas**, a través de `ModrinthService.ApiHashes`. La app los
  calcula en mayúsculas y Modrinth los compara tal cual, respondiendo `{}` con un `200 OK` sano; hasta
  la 1.12.3 la búsqueda de actualizaciones no había encontrado ninguna y el panel de librerías que
  faltan no se había enseñado nunca.
- **«Todo está actualizado» significa que la tienda lo ha dicho.** `GetLatestVersionsByHashAsync`
  devuelve `null` cuando no ha podido preguntar a Modrinth, y la pestaña dice que no ha podido
  comprobarlo — en vez de hacer pasar un fallo por una buena noticia, que es lo que hacía antes.
- **Lo que ya hay en la carpeta incluye lo que va dentro de sus jars.** `ModIdsProvidedIn` cuenta los
  módulos anidados en `fabric-api` y las librerías que llevan otros mods, y se salta los jars
  desactivados, así que ni el escaneo ni la instalación ofrecen una segunda copia de algo ya cargado.

**Las actualizaciones que encuentra una comprobación las guarda el panel, no las filas.** La lista se
reconstruye desde el disco después de cada actualización, activación o borrado, y las versiones nuevas
vivían solo en las filas que se tiraban — así que actualizar un mod de cinco hacía desaparecer los
otros cuatro hasta la siguiente comprobación. Se guardan por la ruta del jar activado (así desactivar
un mod conserva su actualización) y con el tamaño y la fecha que vio la comprobación (así un jar
cambiado a mano no recibe la actualización encontrada para el archivo que era), y se descartan si
cambian el cargador o la versión de Minecraft del servidor, porque todas las respuestas eran para otra
cosa. **Actualizar todas** las recorre una detrás de otra por el mismo código que el botón de cada
fila, con la lista desactivada mientras tanto; un jar en uso para la tanda, porque significa que el
servidor está en marcha y todos los demás fallarían igual.

## Localización

Todo el texto visible está en `Resources/Strings.resx` (español, idioma neutral/base) más los
archivos satélite `Strings.en.resx`, `Strings.pt.resx`, `Strings.fr.resx`, `Strings.de.resx`. El
código los lee con `Localizer.Get("Clave")` (y `string.Format` para parámetros); el XAML usa la
extensión de marcado `{loc:Loc Clave}`. El idioma activo viene de `AppSettings.Language` y se aplica
en `App.OnFrameworkInitializationCompleted` antes de crear ninguna ventana, por eso cambiar de idioma
requiere reiniciar.
Mira [Cómo contribuir](contributing.es.md) para añadir un idioma o un texto nuevo.
