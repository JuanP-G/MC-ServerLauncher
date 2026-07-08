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
| `Views/` | The `.axaml` windows/dialogs (Avalonia XAML) and their thin code-behind. |
| `Localization/` | The translation system (`Localizer` + `{loc:Loc}` markup extension). |
| `Behaviors/` | Attached behaviors (`AutoScrollBehavior`, MOTD coloring in `MinecraftMotd`). |
| `Controls/` | Custom controls (`Sparkline` for the CPU/RAM mini-charts). |
| `Resources/` | `Strings*.resx` (translations) and `app.ico`. |

> The single value converter, `BoolOpacityConverter`, lives in `ViewModels/` — there is no
> `Converters/` folder.

Data lives **per user** under `%APPDATA%\McServerLauncher\`:

- `servers.json` — the server list and each server's config.
- `settings.json` — global settings (language, Playit agent secret key, last-seen version…).
  Both JSON files are written **atomically** (`AtomicJsonFile`): the previous version is kept as
  `.bak`, and a corrupt file is quarantined as `.bad` and recovered from the `.bak` when possible
  (the user is warned at startup instead of silently losing the list).
- `java\` — Java runtimes the app installs (Temurin/Adoptium).
- `logs\` — the persistent console log (`launcher-yyyy-MM-dd.log`, pruned after 14 days).
- `.secret.key` — the AES-GCM key that encrypts secrets on Linux/macOS (Windows uses DPAPI, so no
  key file there).

Each server's own folder also holds a `backups\` directory with the automatic world backups. There
are no hard-coded machine paths.

## Key services

- **`ServerProcessManager`** — owns the `java` process lifecycle: starts it (no console window),
  redirects stdin/stdout/stderr, re-emits each output line via an event, and stops it cleanly by
  sending `stop` (with a kill fallback).
- **`JavaService`** — detects installed Java versions and, if none is compatible, downloads the
  right Temurin (Adoptium) JRE for the architecture. Used both when creating and when starting a
  server.
- **`MinecraftVersionService`** — reads Mojang's version manifest, resolves the `server.jar`
  download URL and the required Java version, and downloads files.
- **`PlayitApiService`** / **`PlayitPartnerService`** / **`PlayitManager`** — talk to Playit.gg.
  `PlayitPartnerService` runs the third-party **setup-code** flow (`create_agent`) to mint a
  **per-user self-managed agent secret key** from a code the user pastes. The partner **Api-Key is
  never in the app** (it's public + open-source): the call goes through a small proxy (a Cloudflare
  Worker, see `playit-proxy/`) that injects the key server-side. The variant_id/version are public
  and baked in. `PlayitApiService` then uses the returned per-user key (as `agent-key`, set app-wide
  via `SetAgentKey`) to list/create/delete tunnels — falling back to a legacy `playit.toml` secret
  or pasted write key otherwise. `PlayitManager` queries/starts/stops the background Windows/systemd
  service. `PlayitConnection` is the shared connect/disconnect flow used by the tunnel buttons and
  the Settings dialog.
- **`PortService`** — checks which TCP ports are in use, finds a free one, and (via P/Invoke) finds
  the PID listening on a port so a stuck server can be freed.
- **`ServerPropertiesService`**, **`PlayersService`**, **`WhitelistService`** — read/write the
  server's files (`server.properties`, `ops.json`, `banned-players.json`, `whitelist.json`).
- **`ServerCreationService`** — writes the initial files of a new server: `eula.txt`,
  `run.bat`/`user_jvm_args.txt` and a minimal `server.properties` with the chosen port. (The jar
  download is done by `MinecraftVersionService`/`ModLoaderService`/`PaperService` and the port is
  picked by `PortService`, all orchestrated by `CreateServerDialog`.)
- **`ModLoaderService`** / **`PaperService`** — install a mod loader (Fabric/Forge) or a Paper build
  onto an existing server, keeping the world. Known limitation: Fabric's meta endpoint publishes no
  checksums, so its server jar can't be hash-verified like the other sources (Mojang SHA-1, Paper
  SHA-256…); instead the downloaded jar is structurally validated (its `install.properties` must
  match the requested game/loader versions) and discarded on mismatch. Forge's trust assumption:
  its maven publishes a `.sha1` next to each artifact but **from the same server** (no independent
  signatures exist in the Forge ecosystem), so the mandatory hash check protects against
  corruption, not a compromised server; since the installer is *executed*, it is additionally
  validated structurally (it must carry `install_profile.json` or an installer manifest) before
  `java -jar` ever sees it.
- **`ModrinthService`** — searches Modrinth and downloads mods/plugins (filtered by the server's type
  and version), and drives the "check for mod updates" flow.
- **`ServerDetectionService`** — inspects a folder to figure out an existing server's type/version
  when the user adds one that already exists.
- **`ServerIconService`** — generates a server's `server-icon.png`: takes any user image, crops it to
  a centered square and scales it to 64×64 with SkiaSharp. (`ServerViewModel.LoadIcon` is what reads
  it back for the Minecraft-style view.)
- **`WorldBackupService`** — creates and restores zip backups of a server's world folder
  (`<server>/backups/`), pruning old ones past the retention.
- **`CrashReportService`** — reads `crash-reports/*.txt` to pull out the `Description:` line and show
  a human-readable reason for a crash. (The unexpected-exit detection is `ServerProcessManager`'s
  `UnexpectedExit` event; the auto-restart logic lives in `ServerViewModel`.)
- **`ConsoleLogService`** — mirrors every console line to `%APPDATA%\McServerLauncher\logs\` so the
  history survives restarts (14-day retention).
- **`ProcessStatsService`** — samples CPU/RAM of the running `java` process for the live stats and the
  `Sparkline` mini-charts.
- **`ToastService`** — shows the app's own pop-up notifications — always-on-top Avalonia windows in
  the bottom-right corner (titled with the server's name), shown only when the app isn't in focus;
  they work even without OS notification support.
- **`NotificationPreferences`** — decides which notifications are shown, combining the global
  settings (master switch + per-kind: join, leave, death/kill, crash, auto-restart-gave-up) with an
  optional per-server override (`ServerConfig.UseCustomNotifications`). `DeathMessageDetector` spots
  death/kill lines in the console for the deaths notification.
- **`SecretProtector`** — encrypts secrets at rest (DPAPI on Windows, AES-GCM + `.secret.key` on
  Linux/macOS), used for the Playit per-user agent secret key (and the legacy write key). If encryption ever fails, the key is **not**
  persisted (plaintext never lands on disk): it keeps working for the session, the failure goes to
  the daily log, and the user is warned once.
- **`DownloadVerifier`** — the shared checksum verifier for downloads (Mojang SHA-1, Adoptium/Paper
  SHA-256, Modrinth SHA-512/SHA-1), deleting the file on mismatch.
- **`Changelog`** — the per-version "what's new" notes shown after an update (see the flow below).
- **`UpdateService`** — checks GitHub Releases for a newer version and downloads the installer for
  the in-app update. Verification against the release's `SHA256SUMS.txt` asset is **mandatory**: if
  the checksum is missing or unreadable, the silent install is refused and the release page opens
  instead.

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
First time the user connects Playit, `MainViewModel.EnsurePlayitAgentAsync` shows the setup-code
dialog (opens `playit.gg/l/setup-third-party` only on a click), exchanges the pasted code via
`PlayitPartnerService.CreateAgentAsync` for a per-user agent secret key, and stores it encrypted.
When creating a server (or via the "Create tunnel" button), `MainViewModel` calls
`PlayitApiService.EnsureMinecraftTunnelAsync` with that key. The public address is detected
periodically by `ServerViewModel` via `GetAddressForPortAsync`, matching by local port. Compliance
with Playit's third-party rules: the browser only opens on an explicit click, a disclaimer states
the app is not affiliated with Playit, and the user can always reach their Playit account directly.
A self-managed agent forwards traffic only while the agent process runs, so `PlayitAgentRunner`
downloads Playit's official `playitd` binary (once, pinned to the registered version) and runs it as
a hidden child process with `--secret <the per-user key>` while the app is open and connected — the
user installs nothing. Since that native binary is the highest-privilege code the app fetches, it is
**verified against a hard-coded SHA-256** (of the exact pinned version) before it ever runs — on
download and when reusing a cached copy — and deleted/failed on mismatch, just like every other
download (`DownloadVerifier`). One agent serves all the user's tunnels. Not available on macOS
(Playit ships no macOS binary); there the user runs Playit themselves.

### In-app update + what's-new
On startup `MainViewModel.CheckForUpdatesAsync` asks `UpdateService` for the latest release and its
installer asset. The **Update** button (`UpdateNowCommand`) downloads the installer, stops servers,
runs it silently and exits; the installer reinstalls and relaunches the app. After an update,
`MainWindow.Loaded` calls `ShowWhatsNewIfUpdated`, which compares the running version with
`AppSettings.LastVersionSeen` and shows `WhatsNewDialog` (localized) with the notes from
`Changelog` for every version the user hadn't seen yet.

### World backups
`WorldBackupService` zips a server's world into `<server>/backups/` on demand and automatically:
before every start (the main safety net — it also covers Restart and auto-restart after a crash),
after a manual clean stop, and before a restore. It keeps the most recent ones up to the configured
retention. `ServerBackupsView` lists them and can restore any backup (taking a safety backup first).

### Auto-restart after a crash
When a server exits unexpectedly, `ServerProcessManager` raises its `UnexpectedExit` event and
`ServerViewModel` restarts it within a budget (a few attempts inside a stability window) to avoid
crash loops, notifying the user via `ToastService` if the budget is exhausted. `CrashReportService`
reads the server's crash report to add a human-readable reason to that notification.

### System tray
`App` installs a `TrayIcon`. Minimizing keeps the window on the taskbar as usual; closing it with the
**X** hides it to the tray (servers keep running) instead of quitting. The tray menu restores the
window (**Show**) or really quits (**Exit** → `MainWindow.RequestExit`, which runs the clean
shutdown).

### Checking mods/plugins for updates
`ServerModsViewModel` asks `ModrinthService` to identify each installed file on Modrinth and flag the
ones with a newer version; the user updates each with one click (checksum-verified download via
`DownloadVerifier`, preserving its enabled/disabled state).

## Localization

All user-facing text lives in `Resources/Strings.resx` (Spanish, the neutral/base language) plus
satellite files `Strings.en.resx`, `Strings.pt.resx`, `Strings.fr.resx`, `Strings.de.resx`. Code
reads them with `Localizer.Get("Key")` (and `string.Format` for parameters); XAML uses the
`{loc:Loc Key}` markup extension. The active language comes from `AppSettings.Language` and is
applied in `App.OnFrameworkInitializationCompleted` before any window is created, so changing the
language requires a restart. See [Contributing](contributing.md) for how to add a language or a new string.
