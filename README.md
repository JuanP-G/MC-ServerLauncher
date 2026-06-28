# 🎮 MC Server Launcher

Aplicación de escritorio para **Windows** que permite gestionar uno o varios servidores de
**Minecraft** desde una interfaz gráfica moderna, **sin tener que usar archivos `.bat`, ventanas de
consola negras ni editar archivos de configuración a mano**.

Pensada para que cualquiera pueda montar y administrar su servidor de forma sencilla: crear el
servidor, elegir la versión, abrirlo a Internet con Playit.gg, gestionar jugadores y cambiar la
configuración… todo con botones.

> Hecha con **WPF / .NET 9** y la librería de interfaz **WPF-UI** (diseño Fluent, tema oscuro).

---

## ⬇️ Descargar e instalar

1. Ve a la **[última versión (Releases)](https://github.com/JuanP-G/MC-ServerLauncher/releases/latest)**.
2. Descarga **`MC-ServerLauncher-Setup-x.y.z.exe`** y ejecútalo.
3. El asistente crea el acceso directo en el **Escritorio** y el **menú Inicio**.
4. Abre la app y crea o añade tu servidor. **No necesitas instalar .NET ni Java** — la app se encarga.

> La primera vez, Windows puede mostrar un aviso de SmartScreen (app nueva sin firma): pulsa
> *Más información → Ejecutar de todas formas*.

---

## ✨ Funcionalidades

### Gestión de servidores
- **Varios servidores** a la vez, cada uno con su configuración.
- **Crear un servidor nuevo** (Vanilla) automáticamente: eliges nombre, **versión** (lista oficial de
  Mojang), **puerto** y **RAM**, y la app descarga el `server.jar`, **acepta el EULA**, genera el
  `run.bat` y un `server.properties` inicial, y opcionalmente lo arranca para generar el mundo.
- **Añadir** un servidor que ya tengas (apuntando a su carpeta).
- **Eliminar** con opciones de borrar también la carpeta del mundo en disco y/o su túnel de Playit.

### Java automático 🟢
- Cada versión de Minecraft necesita una versión de Java distinta. La app **detecta el Java
  instalado** y, si hace falta otro, **descarga e instala el correcto** (Temurin/Adoptium)
  automáticamente — al crear **y** al iniciar el servidor. Sin configurar rutas.
- Soporta x64, x86 y ARM64 (descarga el Java de la arquitectura adecuada).

### Inicio / parada y estado
- **Iniciar / Detener / Reiniciar** (parada limpia guardando el mundo).
- Si el **puerto está ocupado** por un proceso colgado, te dice cuál es y ofrece **cerrarlo** para
  poder arrancar.
- Estado en vivo: **CPU, RAM, tiempo activo y puerto**.
- Indicadores de color (apagado / iniciando / encendido) en el panel y en la lista lateral.

### Vista estilo Minecraft 🟩
- Muestra el servidor como se ve en el juego: **icono** (`server-icon.png`), **MOTD con colores**,
  **jugadores `conectados/máx`** y **barras de señal** que reflejan si es realmente accesible
  (encendido **y** con túnel activo).
- **Cambiar icono**: haz clic en el icono, elige cualquier imagen y la app la convierte a 64×64 y la
  guarda como `server-icon.png`.

### Consola y comandos
- **Consola en tiempo real**, con texto **copiable** (Ctrl+C o clic derecho → Copiar).
- **Caja de comandos** para enviar comandos al servidor (como en la consola original).
- **Ayuda de comandos**: un botón muestra los comandos más útiles con su explicación; al pulsarlos se
  ponen en la caja listos para completar.

### Jugadores 👥
- Pestaña dedicada con: **conectados** (en vivo), **operadores**, **lista blanca**, **baneados** y
  **quién ha entrado alguna vez**.
- Acciones con botones: **OP / quitar OP / expulsar / banear / desbanear** y gestión de la **lista
  blanca** (añadir/quitar jugadores).

### Configuración visual de `server.properties`
- Edita los ajustes con controles fáciles (listas, interruptores, números) y **una explicación de
  qué hace cada uno**: MOTD, modo de juego, dificultad, máximo de jugadores, PvP, hardcore, vuelo,
  bloques de comandos, distancia de visión/simulación, puerto, modo online, lista blanca, etc.

### Integración con Playit.gg 🌐
- Detecta el **servicio de Playit** en segundo plano y muestra su estado.
- **Detecta y muestra la dirección pública** del túnel (la que usan tus amigos) y permite copiarla.
- Botón para abrir el panel de Playit.gg.
- Puede **crear y eliminar túneles** automáticamente al crear/eliminar servidores *(requiere una
  clave de Playit con permiso de escritura; la app la pide una vez y la guarda)*.

---

## 🚀 Requisitos

- **Windows** 10/11 (x64 o ARM64).
- **.NET 9** para compilar/ejecutar desde código (o usa una versión *self-contained* para no
  instalar nada — ver más abajo).
- **Java**: no hace falta instalarlo a mano; la app lo detecta o lo instala según la versión de
  Minecraft.
- **Playit.gg** *(opcional, para abrir el servidor a Internet)*: basta con tener instalado el
  **agente de Playit** (su servicio en segundo plano) y una cuenta. No se necesita la ruta de
  `playit.exe`.

---

## 🛠️ Compilar y ejecutar

```powershell
git clone https://github.com/JuanP-G/MC-ServerLauncher.git
cd MC-ServerLauncher
dotnet run --project McServerLauncher
```

### Generar un ejecutable para repartir (sin que instalen .NET)

```powershell
# Windows 64 bits (Intel/AMD)
dotnet publish McServerLauncher -c Release -r win-x64 --self-contained

# Windows ARM64
dotnet publish McServerLauncher -c Release -r win-arm64 --self-contained
```

El resultado queda en `McServerLauncher/bin/Release/net9.0-windows/<rid>/publish/`.

---

## 📁 Dónde se guardan los datos

La configuración es **por usuario**, no hay rutas fijas en el código:

- `"%APPDATA%\McServerLauncher\servers.json"` — lista de servidores.
- `"%APPDATA%\McServerLauncher\settings.json"` — ajustes (p. ej. la clave de Playit).
- `"%APPDATA%\McServerLauncher\java\"` — versiones de Java que instala la app.

---

## 💻 Compatibilidad de plataformas

| Plataforma | ¿Funciona? |
|---|---|
| Windows x64 / x86 | ✅ Sí |
| Windows ARM64 | ✅ Sí |
| Linux / macOS | ❌ No (la interfaz usa WPF, exclusivo de Windows) |

Soportar Linux/macOS requeriría reescribir la interfaz con un framework multiplataforma (Avalonia).

---

## 🗺️ Próximas ideas

- Copias de seguridad del mundo (crear/restaurar/programar).
- Gestión de mods/plugins (arrastrar y soltar).
- Servidores Paper / Fabric / Forge.
- Más estadísticas (TPS, gráficas).

---

## ⚠️ Aviso

Al usar Minecraft aceptas el [EULA de Minecraft](https://aka.ms/MinecraftEULA). Este proyecto no está
afiliado a Mojang, Microsoft ni Playit.gg.

---

🤖 Desarrollado con ayuda de [Claude Code](https://claude.com/claude-code).
