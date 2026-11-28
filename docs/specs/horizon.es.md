# Horizon como tipo de servidor: especificación y plan

Estado: borrador, 2026-11-21. Versión en inglés: [horizon.md](horizon.md) (mantener las dos a la par). Sin código escrito. Basado en `origin/main` (c5fd6a2), no en la
copia local, que va 72 commits atrasada.

> **En espera (2026-11-28).** `github.com/horizon-rust` (repositorio y releases) devuelve 404 desde el 25 de
> noviembre aproximadamente, así que no hay fuente oficial de la nightly que fija este plan. La web sigue en
> pie y enlaza al repositorio desaparecido. La causa no está confirmada: el registro DMCA de GitHub (última
> entrada, 25 de noviembre) no tiene ningún aviso que nombre a Horizon, y un comentario en Hacker News dice que
> un bot marcó la organización y que el equipo ha recurrido. **No empieza ninguna fase con código hasta que
> vuelva una fuente oficial.** Forks y espejos de terceros no valen: sus binarios no se pueden verificar y al
> menos uno quita el requisito de tener el juego. Parece que siguen en pie forks de contribuidores
> principales y la retirada puede ser un error, pero ninguno es fuente oficial. Se retoma cuando vuelva
> `github.com/horizon-rust`. La sección 2 describe Horizon tal como era antes de esto.

## 1. Qué es Horizon y por qué encaja

Horizon (`horizon-rust/horizon`) es una reescritura de Minecraft Java 26.3 en Rust. Su servidor,
`horizon-server`, es un único programa nativo: sin Java, un archivo de configuración (`server.toml`), panel
web local y copias de seguridad. Los clientes vanilla 26.3 entran sin cambios.

Por qué interesa al launcher: el objetivo es poder alojar un servidor en ordenadores de gama muy baja.

| Dato (fuente: el propio proyecto) | Valor |
|---|---|
| RAM con 1 jugador / 50 jugadores | 0,7 GB / 1,1 GB (Java: 3,4 a 4,5 GB) |
| Arranque | 8 a 9 s (Java: 15 a 22 s) |
| CPU en reposo | 2 % de un núcleo (ejemplo de su documentación) |
| Ticks | 20 TPS hasta 50 bots; 9 a 11 TPS con 100 bots; tick principal en un solo hilo |

Advertencias que la hoja debe respetar:

- Son medidas **del propio proyecto**. No hay benchmarks independientes. Hay que medirlo nosotros (fase 0).
- El tick medio de Paper es **más corto** a pocos jugadores. Horizon gana en memoria, no en velocidad.
- Hardware mínimo: **no está documentado**. Las cifras salen de un i7-7500U de 4 hilos.
- No hay plugins ni mods de Java. Sus mods son nativos y se ejecutan sin aislamiento.
- No hay Bedrock nativo; Geyser/Floodgate se mencionan solo para el archivo de whitelist.
- Solo Minecraft 26.3. `accept_older_clients` está en desarrollo.
- Licencia: **LGPL-3.0** (confirmada en la fase 0; su web aún dice MIT).
- Proyecto no oficial, sin relación con Mojang ni Microsoft. Mucha actividad (789 issues abiertas): se mueve rápido.

## 2. Hechos técnicos que condicionan el diseño

### 2.1 Distribución
- La release estable `v0.2.97` (2026-10-28) se declara **Outdated**. El servidor actual es la release
  `nightly` (prerelease, etiqueta `nightly`, se reconstruye desde `main` cada pocas horas).
- `v0.2.97` solo tiene servidor para Linux x86-64 (`horizon-server-linux-x86_64.tar.gz`). No hay archivo de
  servidor para Windows en esa release. Hay que ver los assets de `nightly` (fase 0: no se leyeron).
- Los instaladores oficiales (`install.sh`, `install.ps1`) descargan la nightly y verifican firma Ed25519 con
  OpenSSL 3 o, si no, el SHA-256 de GitHub. Instalan `horizon-server` y `horizon-prepare`.
- Plataformas: Linux x86-64 y macOS coinciden en todas las fuentes. Windows x64 sí; Windows ARM64 e Intel Mac
  se contradicen entre páginas.

### 2.2 Archivos del juego (la parte difícil)
Horizon no redistribuye nada de Mojang. Necesita unos «game files» preparados con
`horizon-prepare --prepare --server-jar RUTA`, que:

1. requiere **Java 25** (ejecuta los exportadores de Mojang),
2. requiere el `server.jar` oficial de 26.3 (`downloads.server.url` de `versions/26.3/26.3.json`),
3. por defecto busca el Minecraft del launcher oficial (`--minecraft-dir` para cambiarlo); la documentación
   dice «haber jugado una vez». Hay que comprobar si de verdad hace falta el cliente o basta el jar.

El resultado va a `game-files/`. Un servidor sin cabeza puede recibir una carpeta ya preparada con
`horizon-server import-resources DIR` (necesita release v2026.10.17.07 o posterior).

### 2.3 Primer arranque y configuración
- `horizon-server init` (interactivo) o con banderas:
  `init --eula --world NOMBRE --seed N --port 25565 --whitelist --player X --op X --yes`
  (`--offline` pone `online_mode=false`). Solo un sí explícito escribe `eula=true`.
- `server.toml` es la fuente de verdad; el supervisor escribe `server.properties` vanilla antes de cada
  arranque. Las claves desconocidas son **error** (un typo impide arrancar).
- `horizon-server config --json` y `config set CLAVE=VALOR` (por ejemplo `world.difficulty=hard`).
- Se mantienen los formatos vanilla: `ops.json`, `whitelist.json`, `banned-*.json`, `usercache.json`,
  `eula.txt`, `world/`, `logs/latest.log`. Extra: `whitelist-bypass.txt` (prefijo UUID de Floodgate).

### 2.4 Ejecución y control
- Primer plano: `horizon-server run` (consola por stdin, Ctrl-C o SIGTERM paran limpio).
- Segundo plano: `start | stop | restart | status [--json]`, salida en `logs/supervisor.log`.
- Estado: `status --json` da arriba/abajo, TPS, MSPT, jugadores, chunks, RAM, CPU.
- Ordenes sueltas: `cmd ...`; `players --json`; `whitelist`, `op`, `ban`, `kick`, `backup`, `restore`.
- API de control: puerto libre en 127.0.0.1, dirección y token en `.horizon/api.json`.
- Panel web: `127.0.0.1:8130`, acceso con enlace de un solo uso (`horizon-server panel`).
- Cierre por caída: reinicia a los 5 s con espera creciente, se rinde tras 5 caídas en 10 min.
- **No se encontró la línea de «servidor listo» ni el prompt de consola.** Se capturan en la fase 0.
- Contradicción: la página «hub» dice que `console` (adjuntarse) «llega pronto»; `hosting.md` dice que existe.

### 2.5 Copias y actualizaciones
- `backups/` con zips (mundo, archivos vanilla, `server.toml`); automáticas cada 6 h, se guardan 12.
- `update --check --json`, `update` (descarga, verifica, para, copia, cambia, arranca, espera hasta 5 min,
  deshace solo si falla) y `rollback`.

## 2.6 Resultados de la fase 0 (2026-11-21, nightly `0a875da`, Windows x64)

Probado en una carpeta aislada, solo en 127.0.0.1:25599, con el hash de cada descarga comprobado.

| Pregunta | Resultado |
|---|---|
| Servidor para Windows | **Sí, pero no como archivo de servidor.** La nightly solo publica `horizon-server-linux-x86_64.tar.gz`. En Windows hay que usar `horizon-windows-x64.zip` (77,5 MB, 212 MB descomprimido), que trae `horizon-server.exe`, `horizon-prepare.exe` y `horizon-client.exe`. También hay `horizon-windows-arm64.zip` y `horizon-macos-arm64.zip`. |
| Firma | `SHA256SUMS`, `SHA256SUMS.sig` y `SHA256SUMS.v2.sig` (Ed25519) en la release. El SHA-256 publicado por GitHub coincidió. |
| Licencia | **LGPL-3.0** confirmada (archivo `LICENSE` y API de GitHub). La página web que dice MIT está desfasada. |
| ¿Basta el `server.jar`? | **No.** `horizon-prepare` pide también el **client.jar** de 26.3 (41,5 MB), las librerías y los assets (índice 34, 487 MB en total según Mojang). Sin ellos falla en 0,2 s con «Missing ...26.3.jar». |
| Preparación | `horizon-prepare --prepare --server-jar X --client-jar Y --progress json` terminó en **35 s** con Java 25. Deja 276 MB de archivos preparados más 248 MB de trabajo en `.local`. En esta prueba leyó librerías y assets del `.minecraft` del usuario (solo lectura). Aun así `--check` pide `horizon-prepare sounds` (sonidos opcionales). |
| Progreso | `--progress json` emite líneas `{"fraction": 0.0-1.0}` y `{"message": "..."}`: sirve tal cual para la barra de la app. |
| Línea de «listo» | **Formato vanilla**: `[HH:MM:SS] [Server thread/INFO]: Done (119.379s)! For help, type "help"`. El prefijo de los comandos es `System chat: ` (como en Minecraft 26.3). |
| Arranque | **119 s**, no los 8 a 9 s de su documentación. 82 s fueron «resources loaded» (carga de ítems: 35 s). Era el primer arranque, con la caché de WebAssembly desactivada en Windows («owner/DACL not verified») y 12 hilos. No sabemos si los arranques siguientes son más rápidos. |
| Memoria en reposo | **364 MB** de RSS con 0 jugadores y 0 chunks (pico 365 MB); 20 TPS, MSPT medio 0,7 a 1,0 ms, CPU 0 a 1,5 %. Durante la carga llegó a unos 110 a 120 MB antes de subir. Con `low_memory = true` promete unos 60 MB menos. |
| Estado | `status --json` sirve: `phase` (`starting` y luego `running`), `sample.rss_bytes`, `cpu_percent`, `game.tps`, `mspt`, `players`, `levels`. Hay un proceso supervisor y otro «engine» (`horizon-client.exe` en modo servidor): la app debe contar la RAM de los dos. |
| Parada | `stop` por stdin (y Ctrl-C) guarda y sale con código 0 en menos de 1 s. |
| Carpeta del servidor | Tras `init` solo hay `eula.txt` y `server.toml`; el resto lo crea `run`. Vanilla: `server.properties`, `ops.json`, `whitelist.json`, `world/`, `logs/latest.log`. Pesa 1,3 MB sin mundo explorado. |
| `init --eula` | Con `--no-whitelist --offline --yes` no pregunta nada. La app solo debe pasar `--eula` tras una casilla marcada por el usuario. |

Consecuencias para el diseño:

1. **La preparación pasa de «jar de servidor + Java» a «cliente + servidor + librerías + assets + Java 25»**.
   Que la app lo automatice (decisión del 19-11) significa que **descarga el cliente de Mojang**, lo que ya no
   es lo mismo que descargar el jar de servidor para Vanilla. Hay que decidir si se baja solo lo que
   `horizon-prepare` necesita (jar de cliente, librerías de las 114 entradas del JSON y los assets que lea) o
   si se pide al usuario que tenga 26.3 instalado en su launcher oficial. Ver pregunta 10.
2. **Windows ya no es un bloqueo**, pero el «servidor» es un paquete de 212 MB con el cliente dentro.
3. **«Gama muy baja» no está probado**: 364 MB en reposo es una buena cifra, pero el arranque de 119 s y los
   ~520 MB de disco de la preparación hay que medirlos en una máquina modesta antes de prometer nada.
4. **Señal de «listo»**: se puede usar la línea `Done (...)` como en Vanilla (`Done`), con `status --json`
   (`phase: running`) como respaldo.

## 3. Decisiones tomadas (2026-11-19)

| Tema | Decisión |
|---|---|
| Archivos del juego | **La app lo automatiza**: descarga el `server.jar` de Mojang, usa un Java 25 gestionado por la app y ejecuta `horizon-prepare`. El usuario solo ve progreso. |
| Canal de descarga | **Nightly fijada por hash**: se descarga, se verifica firma/SHA-256 y se guarda qué build es. |
| Alcance de la v1 | **Completo**: editor de `server.toml`, copias con `backup`/`restore`, actualización con `rollback`, estado TPS/RAM. |
| Entregable | Esta hoja y su plan por fases, sin tocar código. |

Heredado de Pumpkin (no hace falta volver a decidirlo): tipo **Experimental** con aviso; `Native = true`;
hash obligatorio (`requireChecksum`); anfitriones de descarga restringidos (`TrustedHosts.GitHubReleaseAssets`).

## 4. Riesgos y preguntas abiertas

1. **Mojang y el `server.jar`.** El usuario no «trae» el jar: la app lo descarga por él. Es lo mismo que ya
   hace para Vanilla, pero Horizon deriva de él los game files. Confirmar que la EULA aceptada en `init`
   cubre ese uso y mostrarla antes de preparar. Los game files **nunca se publican ni se reparten**.
2. **Java 25 gestionado.** `JavaService` ya sabe detectar y descargar runtimes. Hay que ver si ofrece 25 y si
   el runtime puede quedarse solo para preparar, sin que el servidor lo use. Esto choca con la idea de
   «equipos muy bajos»: el JDK ocupa disco y solo se usa una vez. Valorar borrarlo tras preparar.
3. **Nightly como canal.** Cambia cada pocas horas y puede romper. Mitigación: fijar el build probado como
   «último conocido bueno» y actualizar solo con aviso; guardar copia antes (lo hace su `update`).
4. **Windows.** Sin servidor en la estable. Si la nightly tampoco lo trae para Windows, la v1 solo cubriría
   Linux y macOS hasta que lo publiquen: fase 0 lo decide. No es aceptable un tipo que no funciona en la
   plataforma principal de la app.
5. **Listo y consola.** Sin línea de «listo» documentada: usar `status --json` o el API local como señal, y
   solo después, si se captura, la línea de log.
6. **Colisión con servidores de la app.** `server.toml` regenera `server.properties`: la app no debe editar
   `server.properties` en un servidor Horizon (`ServerPropertiesService` ya distingue nativos).
7. **Puertos.** 25565 TCP del juego (entra en el aviso de Firewall y de Playit), panel 8130 y API en
   127.0.0.1: la app nunca los expone. Wake desde Bedrock: no aplica.
8. **Licencia.** LGPL-3.0: ver qué implica enlazar o redistribuir el zip antes de empaquetar nada.
9. **Hardware real.** Medir RAM y CPU en una máquina modesta antes de prometer «gama baja» en la documentación.
10. **Cliente de Mojang (nuevo, fase 0).** `horizon-prepare` necesita `client.jar`, librerías y assets. Opciones:
    (a) la app los baja de los servidores de Mojang con el SHA-1 de la versión; (b) exigir 26.3 instalado en el
    launcher oficial (`--minecraft-dir`); (c) las dos, con (b) como atajo cuando existe.
    **Decidido (2026-11-21): (c).** La app usa primero un 26.3 completo del launcher oficial (`jar`, librerías
    y assets verificados) y, si falta algo, lo descarga de Mojang con el SHA-1 del JSON de la versión, a una
    carpeta propia de la app. Se muestra la EULA antes de descargar. Lo descargado no se reparte ni se publica.
11. **Arranque de 119 s.** Si se repite en arranques posteriores, la señal «listo» necesita un tiempo de espera
    generoso y un mensaje de progreso; el modo Despertar (Wake) y el apagado por inactividad de la app
    dejarían de ser prácticos con Horizon.

## 5. Diseño en la app (sigue el patrón de Pumpkin)

Archivos nuevos (nombres provisionales):

- `Services/HorizonService.cs`: elige la release, verifica y descarga (como `PumpkinService`). Ademas del
  servidor, resuelve `horizon-prepare`.
- `Services/HorizonPrepareService.cs`: orquesta jar de Mojang, Java 25, `horizon-prepare` y progreso;
  reintentos y limpieza si falla; detecta game files ya preparados.
- `Services/HorizonConfig.cs`: lee y escribe `server.toml` (modelo tipado; las claves desconocidas son error
  también aquí) usando `config --json` / `config set` cuando sea posible, para no reimplementar su validación.
- `Services/HorizonControl.cs`: envoltorio de `status --json`, `players --json`, `cmd`, `backup(s)`, `restore`,
  `update --check --json`, `rollback`, con `--dir`.
- `Services/HorizonConsole.cs`: análisis de la salida de consola, como `PumpkinConsole`.
- Recursos: `TypeLogos/horizon.png`, claves nuevas en los 5 resx (el neutro es español).
- Pruebas: `HorizonConfigTests`, `HorizonServiceTests`, `HorizonPrepareTests`, `HorizonConsoleTests`,
  `HorizonRealServerTests` (con variables `MCSL_HORIZON_EXE` y descarga opcional, como `PumpkinRealServerTests`).

Cambios en lo existente: valor nuevo `ServerType.Horizon` al final del enum (es un formato de archivo, no se
reordena); fila en `ServerTypeCatalog` (`Native: true, Experimental: true`, familia `None`, crossplay `None`);
`ServerProvisioningService`, `ServerProcessManager`, `StartPreflight`, `ServerDetectionService`,
`ServerDiskUsage`, `CommandCatalog`, `CrossplayService`, `PlayerFiles`, `ServerViewModel`,
`NativeConfigCard` y `ServerSettingsPages`: allí donde hay una rama para Pumpkin, decidir qué corresponde a Horizon.

## 6. Plan por fases (un PR por fase, en inglés; commits en español)

Cada PR actualiza docs en ambos idiomas, como pide la plantilla. Alcance (scope) sugerido: `horizon`.

**Fase 0 — Spike (sin PR de código; resultado en esta hoja)**
- Ver los assets de `nightly`; confirmar servidor para Windows y su firma.
- Ejecutar `horizon-prepare` a mano; ver si necesita el cliente oficial o solo el jar, qué tamaño ocupa y cuánto tarda.
- Arrancar `horizon-server run` por tuberías y capturar consola, línea de listo y cierre.
- Medir RAM y CPU en la máquina del usuario y, si es posible, en una más modesta.
- Salida: tabla de asuntos resueltos, y la licencia aclarada.

**Fase 1 — Tipo y descarga**
Enum, catálogo, logo, textos (5 resx), `HorizonService` con verificación, selector con la insignia Experimental.
Criterio de hecho: se puede crear una carpeta con el servidor descargado y verificado; las pruebas de
`HorizonServiceTests` pasan (asset por plataforma, hash, host no permitido, release sin firma).

**Fase 2 — Preparación de los archivos del juego**
`HorizonPrepareService`, Java 25 gestionado, descarga del jar, progreso y errores traducidos, EULA mostrada
antes. Criterio: desde cero y sin Java, un servidor queda preparado; un fallo deja la carpeta limpia.

**Fase 3 — Arranque, consola y estado**
Proceso nativo, `init` con banderas desde el asistente, señal de «listo», consola, parada limpia, whitelist y
ops por los archivos vanilla, Firewall y Playit, estado TPS/RAM en la tarjeta.
Criterio: crear, arrancar, entrar con un cliente 26.3, parar. Probado con cliente real.

**Fase 4 — Configuración**
Editor de `server.toml` en el detalle del servidor (sin ventanas emergentes, regla del proyecto), con marca de
«se aplica ahora» o «tras reiniciar»; advertencia de claves inválidas antes de guardar.

**Fase 5 — Copias y actualizaciones**
Pestaña de copias (automáticas, manuales, restaurar con confirmación «Reemplazar el mundo»), comprobar y
aplicar actualización con rollback y registro del build conocido bueno.

**Fase 6 — Cierre**
README y arquitectura (ES/EN), web (`python web/_i18n/build.py`), `docs/articles/contributing*`,
Mermaid de arquitectura, nota en las noticias (feed firmado) si procede y entrada en el registro.

Orden: 0 → 1 → 2 → 3 son secuenciales; 4 y 5 son independientes entre sí tras la 3.

## 7. Pruebas

- Unitarias: elección de asset, análisis de tag/versión, hash, hosts, `HorizonConfig` (ida y vuelta,
  claves desconocidas), `HorizonConsole`, orquestación de preparación con procesos falsos.
- Con el servidor real (opcionales, por variables de entorno): arrancar, `status --json`, `cmd list`, parar.
- Manual, antes de fusionar: ventana real, cliente 26.3 real, máquina modesta, Windows con UAC para el Firewall.
- Verificación de Avalonia con el arnés de capturas (compilar no prueba nada con `CompileBindings=False`).

## 8. Fuera de alcance (por ahora)

Mods nativos de Horizon, proxies (Velocity/transferencia), Discord integrado de Horizon, RCON/GS4/protocolo de
gestión, Bedrock, clientes 26.2, Linux ARM (se compila desde fuente), servicio de Windows de Horizon (la app ya
gestiona el arranque), el cliente y el launcher de Horizon.

## 9. Fuentes

- https://github.com/horizon-rust/horizon (README, `docs/hosting.md`, `docs/server.md`)
- https://playhorizon.dev/docs/hosting y https://playhorizon.dev/hub
- https://api.github.com/repos/horizon-rust/horizon/releases/latest (v0.2.97)

No leídos del todo: la mitad final de `hosting.md` y `server.md`, `docs/api/server-reference.md`,
`install.sh`/`install.ps1`, los assets de `nightly`, `docs/telemetry.md`. La fase 0 los cierra.
