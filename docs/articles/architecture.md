# Architecture

> 🇪🇸 ¿Prefieres español? Lee la versión en [español](architecture.es.md).

MC Server Launcher is a **Avalonia / .NET 9** desktop app that follows the **MVVM** pattern
(using [CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/)) with the
[Avalonia](https://avaloniaui.net/) Fluent theme (cross-platform). It manages one or more Minecraft servers without
`.bat` files, console windows or editing config files by hand.

## Layers

The project (`McServerLauncher/`) is organized by responsibility:

| Folder | Responsibility |
|---|---|
| `Models/` | Plain data: persisted config (`ServerConfig`), settings (`AppSettings`), enums (`ServerState`, `PlayitState`). |
| `Services/` | All the logic with no UI: processes, files, network, Java, Playit, ports, etc. Each service is a small, focused class. |
| `ViewModels/` | The state and commands the UI binds to (`MainViewModel`, `ServerViewModel`). No Avalonia controls here, only `ObservableObject`/`RelayCommand`. |
| `Views/` | The XAML windows/dialogs and their thin code-behind. |
| `Localization/` | The translation system (`Localizer` + `{loc:Loc}` markup extension). |
| `Behaviors/`, `Converters/` | Small UI helpers (auto-scroll, MOTD coloring, bool→visibility). |
| `Resources/` | `Strings*.resx` (translations) and `app.ico`. |

Data lives **per user** under `%APPDATA%\McServerLauncher\`: `servers.json` (the server list),
`settings.json` (global settings) and `java\` (Java runtimes the app installs). There are no
hard-coded machine paths.

## Key services

- **`ServerProcessManager`** — owns the `java` process lifecycle: starts it (no console window),
  redirects stdin/stdout/stderr, re-emits each output line via an event, and stops it cleanly by
  sending `stop` (with a kill fallback).
- **`JavaService`** — detects installed Java versions and, if none is compatible, downloads the
  right Temurin (Adoptium) JRE for the architecture. Used both when creating and when starting a
  server.
- **`MinecraftVersionService`** — reads Mojang's version manifest, resolves the `server.jar`
  download URL and the required Java version, and downloads files.
- **`PlayitApiService`** / **`PlayitManager`** — talk to Playit.gg: read the public tunnel address
  (via the agent's read-only key) and create/delete tunnels (with a user-provided write key);
  `PlayitManager` queries/starts/stops the background Windows service.
- **`PortService`** — checks which TCP ports are in use, finds a free one, and (via P/Invoke) finds
  the PID listening on a port so a stuck server can be freed.
- **`ServerPropertiesService`**, **`PlayersService`**, **`WhitelistService`** — read/write the
  server's files (`server.properties`, `ops.json`, `banned-players.json`, `whitelist.json`).
- **`UpdateService`** — checks GitHub Releases for a newer version and downloads the installer for
  the in-app update.

## Important flows

### Starting a server
`ServerViewModel.Start` → refresh port/info → if the port is busy, offer to free it
(`PortService` + `TryFreePortAsync`) → `EnsureCompatibleJavaAsync` (uses `JavaService` to read the
required Java from the jar and install it if needed) → `ServerProcessManager.Start`. Console output
streams back through the `OutputReceived` event into `ConsoleLines`.

### Java auto-install
At **create** time, `CreateServerDialog` asks `MinecraftVersionService` for the required Java major
and calls `JavaService.EnsureJavaAsync`. At **start** time, `ServerViewModel` reads the Java version
embedded in `server.jar` (`version.json`) and installs/uses a compatible runtime, saving the path in
`ServerConfig.JavaPath`.

### Playit tunnel
When creating a server (or via the "Create tunnel" button), `MainViewModel` calls
`PlayitApiService.EnsureMinecraftTunnelAsync` with the write key. The public address is detected
periodically by `ServerViewModel` via `GetAddressForPortAsync`, matching by local port.

### In-app update + what's-new
On startup `MainViewModel.CheckForUpdatesAsync` asks `UpdateService` for the latest release and its
installer asset. The **Update** button (`UpdateNowCommand`) downloads the installer, stops servers,
runs it silently and exits; the installer reinstalls and relaunches the app. After an update,
`MainWindow.Loaded` calls `ShowWhatsNewIfUpdated`, which compares the running version with
`AppSettings.LastVersionSeen` and shows `WhatsNewDialog` (localized) when it changed.

## Localization

All user-facing text lives in `Resources/Strings.resx` (Spanish, the neutral/base language) plus
satellite files `Strings.en.resx`, `Strings.pt.resx`, `Strings.fr.resx`, `Strings.de.resx`. Code
reads them with `Localizer.Get("Key")` (and `string.Format` for parameters); XAML uses the
`{loc:Loc Key}` markup extension. The active language comes from `AppSettings.Language` and is
applied in `App.OnStartup` before any window is created, so changing the language requires a
restart. See [Contributing](contributing.md) for how to add a language or a new string.
