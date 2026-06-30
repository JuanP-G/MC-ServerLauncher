# 🎮 MC Server Launcher

**🇬🇧 [English](#-english) · 🇪🇸 [Español](#-español)**

---

## 🇬🇧 English

Desktop app for **Windows** that lets you manage one or several **Minecraft** servers from a modern
graphical interface, **without `.bat` files, black console windows or editing config files by hand**.

Designed so anyone can set up and run their server easily: create the server, pick the version, open
it to the Internet with Playit.gg, manage players and change the configuration… all with buttons.

> Built with **Avalonia / .NET 9** (cross-platform, Fluent design, dark theme).

### ⬇️ Download & install

1. Go to the **[latest release](https://github.com/JuanP-G/MC-ServerLauncher/releases/latest)**.
2. Download **`MC-ServerLauncher-Setup-x.y.z.exe`** and run it.
3. The wizard creates the shortcut on the **Desktop** and the **Start menu**.
4. Open the app and create or add your server. **You don't need to install .NET or Java** — the app handles it.
5. From version 1.0.2 on, **updates happen inside the app**: when a new version exists, a banner shows
   an **Update** button that downloads and installs it for you. After updating, a **What's new** window
   tells you what changed.

> The first time, Windows may show a SmartScreen warning (new, unsigned app): click
> *More info → Run anyway*.

### ✨ Features

- **Multiple servers** at once, each with its own configuration.
- **Create a new server** automatically (Vanilla or Fabric): name, **version** (official Mojang list), **port**
  and **RAM**; the app downloads `server.jar`, **accepts the EULA**, generates `run.bat` and an initial
  `server.properties`, and can start it to generate the world.
- **Add** an existing server, or **delete** one (optionally removing its world folder and Playit tunnel).
- **Automatic Java** 🟢 — detects the installed Java and, if needed, downloads and installs the correct
  one (Temurin/Adoptium), both when creating **and** starting. Supports x64, x86 and ARM64.
- **Start / Stop / Restart** with a clean stop that saves the world. If the **port is busy** by a stuck
  process, it tells you which one and offers to close it. Live **CPU, RAM, uptime and port**, with
  colour status (off / starting / on).
- **Minecraft-style view** 🟩 — shows the server icon, coloured MOTD, `players/max` and signal bars that
  reflect whether it's actually reachable (running **and** with an active tunnel). Click the icon to
  change it from any image.
- **Real-time console** with copyable text (Ctrl+C or right-click → Copy), a **command box**, and a
  **command help** button that explains the most useful commands.
- **Players** 👥 — a tab with connected (live), operators, whitelist, banned and "ever joined", plus
  buttons: OP / de-OP / kick / ban / unban and whitelist management.
- **Visual `server.properties` editor** — easy controls with an explanation of what each setting does.
- **Playit.gg integration** 🌐 — detects the background service, shows and copies the public address,
  and can create/delete tunnels automatically *(creating tunnels needs a Playit key with write
  permission; the app asks for it once and saves it)*.
- **Multi-language** — Spanish, English, Portuguese, French and German, including console messages and
  dialogs. Change it from the sidebar (applied after a restart).

### 🚀 Requirements

- **Windows** 10/11 (x64 or ARM64).
- **.NET 9** only to build/run from source (or use a *self-contained* build to install nothing).
- **Java**: no manual install needed; the app detects or installs it per Minecraft version.
- **Playit.gg** *(optional, to open the server to the Internet)*: just have the **Playit agent**
  (its background service) installed and an account. The `playit.exe` path is not needed.

### 🛠️ Build & run

```powershell
git clone https://github.com/JuanP-G/MC-ServerLauncher.git
cd MC-ServerLauncher
dotnet run --project McServerLauncher

# Self-contained executable (users don't install .NET):
dotnet publish McServerLauncher -c Release -r win-x64 --self-contained     # Intel/AMD 64-bit
dotnet publish McServerLauncher -c Release -r win-arm64 --self-contained   # ARM64
```

### 📖 Documentation (for contributors)

Developer documentation — architecture, contributing guide and a full **API reference** generated
from the code — is published with **DocFX** at:

**https://juanp-g.github.io/MC-ServerLauncher/**

The guide is available in English and Spanish. The source lives in [`docs/`](docs/); build it locally
with `.\docs\build-docs.ps1`.

### 📁 Where data is stored

Per-user, no hard-coded paths: `"%APPDATA%\McServerLauncher\servers.json"` (servers),
`settings.json` (settings, e.g. the Playit key) and `java\` (Java versions the app installs).

### 💻 Platform support

| Platform | Works? |
|---|---|
| Windows x64 / x86 | ✅ Yes |
| Windows ARM64 | ✅ Yes |
| Linux x64 | ✅ Yes (AppImage) |
| macOS | ⚙️ Builds from source (no prebuilt package yet) |

> The UI is built with **Avalonia**, so a single codebase runs on Windows, Linux and macOS.
> On Linux, download the **`.AppImage`** from the release, make it executable (`chmod +x`) and run it.

### ⚠️ Notice

By using Minecraft you accept the [Minecraft EULA](https://aka.ms/MinecraftEULA). This project is not
affiliated with Mojang, Microsoft or Playit.gg.

---

## 🇪🇸 Español

Aplicación de escritorio para **Windows** que permite gestionar uno o varios servidores de
**Minecraft** desde una interfaz gráfica moderna, **sin tener que usar archivos `.bat`, ventanas de
consola negras ni editar archivos de configuración a mano**.

Pensada para que cualquiera pueda montar y administrar su servidor de forma sencilla: crear el
servidor, elegir la versión, abrirlo a Internet con Playit.gg, gestionar jugadores y cambiar la
configuración… todo con botones.

> Hecha con **Avalonia / .NET 9** (multiplataforma, diseño Fluent, tema oscuro).

### ⬇️ Descargar e instalar

1. Ve a la **[última versión (Releases)](https://github.com/JuanP-G/MC-ServerLauncher/releases/latest)**.
2. Descarga **`MC-ServerLauncher-Setup-x.y.z.exe`** y ejecútalo.
3. El asistente crea el acceso directo en el **Escritorio** y el **menú Inicio**.
4. Abre la app y crea o añade tu servidor. **No necesitas instalar .NET ni Java** — la app se encarga.
5. A partir de la versión 1.0.2, **las actualizaciones se hacen dentro de la app**: cuando hay una
   versión nueva, un aviso muestra un botón **Actualizar** que la descarga e instala por ti. Tras
   actualizar, una ventana de **Novedades** te cuenta qué ha cambiado.

> La primera vez, Windows puede mostrar un aviso de SmartScreen (app nueva sin firma): pulsa
> *Más información → Ejecutar de todas formas*.

### ✨ Funcionalidades

- **Varios servidores** a la vez, cada uno con su configuración.
- **Crear un servidor nuevo** automáticamente (Vanilla o Fabric): eliges nombre, **versión** (lista oficial de
  Mojang), **puerto** y **RAM**, y la app descarga el `server.jar`, **acepta el EULA**, genera el
  `run.bat` y un `server.properties` inicial, y opcionalmente lo arranca para generar el mundo.
- **Añadir** un servidor que ya tengas, o **eliminar** uno (con opción de borrar su carpeta del mundo
  y su túnel de Playit).
- **Java automático** 🟢 — detecta el Java instalado y, si hace falta otro, **descarga e instala el
  correcto** (Temurin/Adoptium), al crear **y** al iniciar. Soporta x64, x86 y ARM64.
- **Iniciar / Detener / Reiniciar** con parada limpia que guarda el mundo. Si el **puerto está ocupado**
  por un proceso colgado, te dice cuál es y ofrece cerrarlo. Estado en vivo de **CPU, RAM, tiempo activo
  y puerto**, con indicadores de color (apagado / iniciando / encendido).
- **Vista estilo Minecraft** 🟩 — muestra el icono, el MOTD con colores, los `jugadores/máx` y barras de
  señal que reflejan si es realmente accesible (encendido **y** con túnel activo). Cambia el icono con
  clic desde cualquier imagen.
- **Consola en tiempo real** con texto copiable (Ctrl+C o clic derecho → Copiar), **caja de comandos** y
  un botón de **ayuda de comandos** que explica los más útiles.
- **Jugadores** 👥 — pestaña con conectados (en vivo), operadores, lista blanca, baneados y "han entrado
  alguna vez", más botones: OP / quitar OP / expulsar / banear / desbanear y gestión de la lista blanca.
- **Configuración visual de `server.properties`** — controles fáciles con una explicación de qué hace
  cada ajuste.
- **Integración con Playit.gg** 🌐 — detecta el servicio en segundo plano, muestra y copia la dirección
  pública, y puede crear/eliminar túneles automáticamente *(crear túneles requiere una clave de Playit
  con permiso de escritura; la app la pide una vez y la guarda)*.
- **Multi-idioma** — español, inglés, portugués, francés y alemán, incluidos los mensajes de la consola
  y los diálogos. Se cambia desde la barra lateral (se aplica al reiniciar).

### 🚀 Requisitos

- **Windows** 10/11 (x64 o ARM64).
- **.NET 9** solo para compilar/ejecutar desde código (o usa una versión *self-contained* para no
  instalar nada).
- **Java**: no hace falta instalarlo a mano; la app lo detecta o lo instala según la versión de Minecraft.
- **Playit.gg** *(opcional, para abrir el servidor a Internet)*: basta con tener instalado el **agente de
  Playit** (su servicio en segundo plano) y una cuenta. No se necesita la ruta de `playit.exe`.

### 🛠️ Compilar y ejecutar

```powershell
git clone https://github.com/JuanP-G/MC-ServerLauncher.git
cd MC-ServerLauncher
dotnet run --project McServerLauncher

# Ejecutable self-contained (sin que instalen .NET):
dotnet publish McServerLauncher -c Release -r win-x64 --self-contained     # Intel/AMD 64 bits
dotnet publish McServerLauncher -c Release -r win-arm64 --self-contained   # ARM64
```

### 📖 Documentación (para colaboradores)

La documentación de desarrollo — arquitectura, guía de contribución y una **referencia de API**
completa generada a partir del código — se publica con **DocFX** en:

**https://juanp-g.github.io/MC-ServerLauncher/**

La guía está disponible en inglés y español. El origen está en [`docs/`](docs/); genérala en local
con `.\docs\build-docs.ps1`.

### 📁 Dónde se guardan los datos

Por usuario, sin rutas fijas en el código: `"%APPDATA%\McServerLauncher\servers.json"` (servidores),
`settings.json` (ajustes, p. ej. la clave de Playit) y `java\` (versiones de Java que instala la app).

### 💻 Compatibilidad de plataformas

| Plataforma | ¿Funciona? |
|---|---|
| Windows x64 / x86 | ✅ Sí |
| Windows ARM64 | ✅ Sí |
| Linux x64 | ✅ Sí (AppImage) |
| macOS | ⚙️ Se compila desde el código (aún sin paquete prehecho) |

> La interfaz usa **Avalonia**, así que un único código corre en Windows, Linux y macOS.
> En Linux, descarga el **`.AppImage`** de la release, dale permiso de ejecución (`chmod +x`) y ábrelo.

### ⚠️ Aviso

Al usar Minecraft aceptas el [EULA de Minecraft](https://aka.ms/MinecraftEULA). Este proyecto no está
afiliado a Mojang, Microsoft ni Playit.gg.

---

🤖 Desarrollado con ayuda de [Claude Code](https://claude.com/claude-code).
