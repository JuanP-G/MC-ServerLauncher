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
| `Models/` | Plain data: persisted config (`ServerConfig`), settings (`AppSettings`), enums (`ServerState`, `PlayitState`). Two subfolders hold the shapes that come from elsewhere: `Modrinth/` (what the API returns) and `Store/` (what the store shows, independent of where it came from). |
| `Services/` | All the logic with no UI: processes, files, network, Java, Playit, ports, etc. Each service is a small, focused class. |
| `ViewModels/` | The state and commands the UI binds to (`MainViewModel`, `ServerViewModel`, and one per panel: `ServerModsViewModel`, `ServerBackupsViewModel`, `ModDetailsViewModel`). Bindable state and `RelayCommand`s, not Avalonia controls. |
| `Views/` | The `.axaml` windows/dialogs (Avalonia XAML) and their thin code-behind. |
| `Localization/` | The translation system (`Localizer` + `{loc:Loc}` markup extension). |
| `Behaviors/` | Attached behaviors: `AutoScrollBehavior` (the console follows the tail), `ResetScrollBehavior` (a list goes back to the top when its contents are *replaced*, not appended), MOTD coloring in `MinecraftMotd` and Markdown rendering in `MarkdownBody`. |
| `Controls/` | Custom controls (`Sparkline` for the CPU/RAM mini-charts). |
| `Styles/` | `Shared.axaml`: the styles more than one view needs (`Border.card`, `Border.tile`, the stat text…), included from `App.axaml`. |
| `Resources/` | `Strings*.resx` (translations), `app.ico`, and the store's two data files (`store-tags.json`, `store-summaries.json`). |

> **Value converters live in `ViewModels/`** — there is no `Converters/` folder. There are five:
> `BoolOpacityConverter`, `HexBrushConverter` (a hex string from the settings into a brush),
> `ConsoleBrushConverter` and `ConsoleHighlightConverter` (the colour of a console line and the
> marking of what was searched for), and `NoticeBrushConverter` (the install banner's background).
>
> The UI-free half of each colour decision is kept out of `ViewModels/` on purpose, so a setting
> that is serialized to `settings.json` never has to know Avalonia exists: `ConsoleColors` /
> `ConsolePalette`, `NotificationPalette` / `NotificationBrushes` and `ServerTypeCatalog` /
> `ServerTypeBrushes` are three instances of the same split — hex strings in `Services/`, brushes in
> `ViewModels/`.

> **`ServerConfig` announces its own changes, and the view models share the instance.** It and
> `NotificationSettings` are the two `Models/` types that do — both are edited in place by a dialog
> while something else is on screen showing them. They were plain objects, on the argument that a
> persisted model should not depend on MVVM; that argument does not survive contact with the fact
> that `Models/` and `ViewModels/` are folders in one assembly, so the dependency was already there,
> and what the rule actually bought was a class of bug. Everything derived from the config simply
> never recomputed: converting a server changed the disk and left the app describing what the folder
> used to be until it was restarted.
>
> **This does not change `servers.json`.** Reflection-based System.Text.Json writes public
> properties; the generated ones keep the exact names the auto-properties had; `ObservableObject`
> contributes only events, which are not serialized. The key *order* is not promised, and nothing
> reads the file positionally. `ServerConfigFormatTests` holds every part of that down — the exact
> key set, `Type` still being the integer that is the file format, the two `[JsonIgnore]` computed
> paths staying out, and a file written by an older version still opening.
>
> Never give either class a `Clone` built on `MemberwiseClone`: it copies the `PropertyChanged`
> delegate, so the copy raises changes at the original's subscribers. `NotificationSettings.Clone`
> is field-by-field, and `AppSettings` — which does use `MemberwiseClone` — is deliberately left a
> plain object, because the settings dialog edits a copy and commits it on OK instead.

> **One table says what each field of the config feeds.** `ServerConfigEffects` has a row per
> `ServerConfig` property: which `ServerViewModel` and `ServerModsViewModel` properties to announce,
> and what has to be redone that a notification cannot express — re-read the port, rescan the
> content folder, close the store's details page, search again, reopen the wake listener, reload the
> backups. `ServerViewModel` subscribes to `Config.PropertyChanged` once and applies the row, on the
> UI thread (crossplay writes the Bedrock port from a background continuation). A field nothing
> shows gets a row too, carrying the reason.
>
> **`ServerConfigEffectsTests` is what makes this hold.** Every writable property of `ServerConfig`
> must appear exactly once, and every view-model property a row names must still exist. A field
> added without a row fails on the day it is written, and a rename that misses the table fails
> instead of announcing a name nothing listens for. That is the whole point: the bug was never hard,
> it was just quiet.
>
> Persisting is deliberately not one of the effects. The edit dialog writes into the live config as
> the user types, so saving on every change would rewrite `servers.json` on every keystroke; saving
> stays where it is, once, when a dialog is accepted.
>
> For the same reason the folder box in `AddEditServerDialog` is the one binding with
> `UpdateSourceTrigger=LostFocus`. The folder is the server's whole identity on disk, and committing
> it per keystroke would re-read the port, the MOTD, the icon, the content folder and the backup
> list once per letter, against paths that do not exist yet. Every other box there commits as you
> type, which is what makes the card update while you edit it.

> **A constructor assembles; `Activate()` starts.** `ServerViewModel` and `MainViewModel` each split
> in two. The constructor reads — the config, the console palette, the server's own files — and
> leaves nothing running behind it. `Activate()` is everything that reaches outside the object: the
> polling timers, the shared `PlayitManager` / `PlayitAgentRunner` subscriptions, the tunnel
> lookups, the wake-on-demand socket, the update check. `MainWindow` calls
> `MainViewModel.Activate()` from `Loaded`, and that reaches every server; a server registered later
> is activated by `Register` on the spot.
>
> `ShutdownAsync()` is the exact mirror, and both are safe to call twice — `Loaded` fires again
> every time the window comes back from the tray. This is why the two can be built in a test at all:
> before the split, constructing one started three timers, opened a socket and called the network,
> so nothing that touched them could be tested except through its pure pieces.

Data lives **per user** under `%APPDATA%\McServerLauncher\` (`~/.config/McServerLauncher/` on Linux
and macOS):

- `servers.json` — the server list and each server's config.
- `settings.json` — global settings (language, Playit agent secret key, last-seen version…).
  Both JSON files are written **atomically** (`AtomicJsonFile`): the previous version is kept as
  `.bak`, and a corrupt file is quarantined as `.bad` and recovered from the `.bak` when possible
  (the user is warned at startup instead of silently losing the list).
- `java\` — Java runtimes the app installs (Temurin/Adoptium).
- `logs\` — the persistent console log (`launcher-yyyy-MM-dd.log`, pruned after 14 days).
- `cache\images\` and `cache\store\` — the store's disk caches: project icons and gallery
  screenshots (`ImageCache`, pruned after 30 days) and the API responses (`StoreCache`). Both are
  disposable; deleting them costs a few requests and nothing else.
- `playit-agent\` — Playit's official `playitd` binary, downloaded once and pinned to the version
  the app registers (`PlayitAgentRunner`).
- `instance.lock` — the exclusive file lock that keeps the app to one running copy per user
  (`SingleInstance`).
- `.secret.key` — the AES-GCM key that encrypts secrets on Linux/macOS (Windows uses DPAPI, so no
  key file there).
- *(optional)* `store-tags.json` / `store-summaries.json` — if either exists it replaces the copy
  embedded in the app, so tags and plain-language summaries can be changed without a new build.

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
  Worker, see `playit-proxy/`) that injects the key server-side. The Worker only accepts the
  create-agent POST shape the desktop app sends, rejects browser-origin traffic, and can rate-limit
  per IP. The variant_id/version are public and baked in. `PlayitApiService` then uses the returned
  per-user key (as `agent-key`, set app-wide via `SetAgentKey`) to list/create/delete tunnels —
  falling back to a legacy `playit.toml` secret
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
- **`ServerTypeCatalog`** — one row per server type: display name, family (plugins/mods/neither), badge colour and
  its `CrossplayLevel`. The picker, the badges, the mod store, the content folder and the crossplay rules all
  read from it, so adding a type is a row rather than six `switch` statements found by hand.
  The level is three-valued rather than a yes/no, because "Geyser publishes a build" and "your friend on a phone
  can play" are different claims: `Full` for Paper, Purpur and Fabric, `Partial` for NeoForge — it connects and
  authenticates, and then any mod the client is required to have shuts Bedrock out — and `None` for Vanilla and
  Forge. `CrossplayService.CaveatKey` turns the level into the note both dialogs show.
- **`ServerJarInstaller`** — the one place that knows how each type is obtained. The create dialog and the
  change-type dialog both call it; the chain used to be written out inline in both, and a type present in one and
  missing from the other silently produced a Vanilla server.
- **`ModLoaderService`** / **`PaperService`** / **`PurpurService`** — install a mod loader (Fabric/Forge/NeoForge) or a
  Paper/Purpur build. Purpur publishes only an MD5 for its builds, not a SHA-256; HTTPS authenticates the source and
  the hash is there to catch a corrupted download, which is documented in the service itself. Also
  installs a loader onto an existing server, keeping the world. Known limitation: Fabric's meta endpoint publishes no
  checksums, so its server jar can't be hash-verified like the other sources (Mojang SHA-1, Paper
  SHA-256…); instead the downloaded jar is structurally validated (its `install.properties` must
  match the requested game/loader versions) and discarded on mismatch. Forge's trust assumption:
  its maven publishes a `.sha1` next to each artifact but **from the same server** (no independent
  signatures exist in the Forge ecosystem), so the mandatory hash check protects against
  corruption, not a compromised server; since the installer is *executed*, it is additionally
  validated structurally (it must carry `install_profile.json` or an installer manifest) before
  `java -jar` ever sees it. NeoForge is the same shape with a better hash: its maven publishes a
  `.sha256` beside each artifact, so that is what is checked. The trust assumption is unchanged —
  same server as the jar — and a missing hash still means no install, because what follows is
  `java -jar`. Which build belongs to which Minecraft version is decided by `NeoForgeVersions`,
  separately from the download: NeoForge has no promotions feed, so the mapping is derived from the
  build number and is unit-tested on its own.
- **`ModrinthService`** — searches Modrinth and downloads mods/plugins (filtered by the server's type
  and version), and drives the "check for mod updates" flow.
- **`ModDependencyService`** — walks a version's *required* dependencies, transitively, and says which are
  missing. Two facts about Modrinth's data shape it: dependencies carry **no version range** (a dependency
  either pins one version id or names a project), which is why "that project is already installed" is a
  complete answer rather than an approximation; and `embedded` means the dependency is already inside the jar,
  so installing it again produces the loader's *duplicate mod* failure. The walk itself (`WalkAsync`) takes its
  lookup as a delegate, so what it decides is tested against a table rather than against Modrinth on the day
  the test runs.
- **`ContentManifest` / `ContentDependencyCheck`** — read what each jar declares about itself (what it provides
  and what it needs) and report what is missing. Three formats: `fabric.mod.json`, Bukkit's `plugin.yml` and
  Forge/NeoForge's `mods.toml`, with no YAML or TOML library — only lists of names are needed, and anything not
  understood counts as "declares nothing". **Jars inside jars count**: both loaders let a mod carry others
  (`META-INF/jars/`, `META-INF/jarjar/`), and `fabric-api` is forty-odd modules shipped that way, so what a
  nested jar provides is added to the outer one — followed a few levels down — while what it requires is not,
  since a gap inside a bundle is the loader's to report. The Mods tab's own dependency reader goes through the
  same code rather than a second copy of it; there used to be two, and only one had learnt this.
  **No network, deliberately**: this is the check that runs when Start
  is pressed, and the Modrinth calls that would answer the same question swallow their errors and return empty,
  so offline they would report nothing missing on the one screen where being wrong stops the server coming up.
  A test forbids those two files from mentioning `HttpClient` or `ModrinthService`.
- **`NotificationCatalog` / `NotificationPalette`** — which level and which emoji each kind of notification
  gets, and what the default colours are. The same split as `ServerTypeCatalog` and `ServerTypeBrushes`: the
  UI-free data here, the Avalonia brushes in `NotificationBrushes`. The colours the user can change live in
  `NotificationSettings`, which is serialized to `settings.json`.
- **`ConsoleLineClassifier` / `ConsoleColors`** — what each console line is about and what colour it is drawn
  in. The source outranks the text: the app's own messages are tagged where they are raised, because their text
  is localized — the `[Launcher]`, `[Error]` and `[Players]` prefixes live *inside* the resx values — so a
  classifier keyed on them would work in Spanish and quietly stop working in German. `stderr` arrives tagged
  from `ServerProcessManager`, which used to merge it with standard output in one handler, and is red except
  for the JVM's own `WARNING:` lines, whose format Java fixes. Only `stdout` is read: vanilla's bracket (level
  in the **second** one, not the first) and Paper's. **A line with no log prefix belongs to the entry above
  it** — Fabric's list of mods, the lines under a multi-line warning, the message of an exception — so it
  takes that entry's level rather than being judged alone; the view model passes the previous stdout line's
  kind in. Stack frames are recognised by their shape (`at x.y(File:1)`, `Caused by:`, `... 12 more`), never
  by indentation: the loader indents its mod list with tabs, and a tab used to paint every mod red.
- **`BlueMapConsent`** and `ServerProcessManager.ImpliedJvmFlags` — the two start-up warnings the app
  answers itself, because they are its to answer. From Java 22 it adds `--enable-native-access=ALL-UNNAMED`
  to the command line it builds: JNA-based mods trigger a warning that says a future Java will *block* the
  call, and an unrecognised option would stop an older JVM from starting, hence the version gate.
  `--sun-misc-unsafe-memory-access` is deliberately not added — its accepted values are changing release by
  release. And when BlueMap prints that its download has not been accepted, the app **asks** — it is consent
  to fetch Mojang's client files — and on a yes flips that one value in `core.conf` and sends
  `bluemap reload`. The other warnings a modded start prints (refmaps, mixins aimed at absent mods, a Windows
  registry value, Distant Horizons recommending ZGC for client FPS) are not the app's to change.
- **`ServerDetectionService`** — inspects a folder to figure out an existing server's type/version
  when the user adds one that already exists. It runs twice: on the way in from *Add server*, and
  again at startup for servers saved before those fields existed. It fills nothing in a config that
  already names its version, so the second pass is free and neither can overrule the user.
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
  optional per-server override (`ServerConfig.UseCustomNotifications`). Per-server overrides are
  cloned field-by-field from the global defaults so later changes don't share mutable state.
  `DeathMessageDetector` spots death/kill lines in the console for the deaths notification, requiring
  a valid player-name subject followed immediately by a known vanilla death phrase to reduce chat or
  plugin false positives.
- **`SecretProtector`** — encrypts secrets at rest (DPAPI on Windows, AES-GCM + `.secret.key` on
  Linux/macOS), used for the Playit per-user agent secret key (and the legacy write key). If encryption ever fails, the key is **not**
  persisted (plaintext never lands on disk): it keeps working for the session, the failure goes to
  the daily log, and the user is warned once.
- **`DownloadVerifier`** — the shared checksum verifier for downloads (Mojang SHA-1, Adoptium/Paper
  SHA-256, Modrinth SHA-512/SHA-1), deleting the file on mismatch.
- **`Changelog`** — the per-version "what's new" notes shown after an update (see the flow below).
- **`UpdateService`** / **`SelfUpdater`** — `UpdateService` asks GitHub for a newer release and picks
  the asset for *this* platform and architecture (the Windows installer, the Linux AppImage, the
  macOS `.dmg`); `SelfUpdater` is what applies it. It reads the release **list**, not
  `/releases/latest`, because GitHub leaves pre-releases out of the latter and a beta published that
  way would be invisible to the app. Verification against the release's `SHA256SUMS.txt` asset is
  **mandatory**: if the checksum is missing or unreadable, the in-place update is refused and the
  release page opens instead.

### Supporting services

- **`AppSettingsService`** / **`ServerStorageService`** — the two owners of the app's JSON:
  `settings.json` and `servers.json`. Both go through `AtomicJsonFile` and both report what happened
  on load, so a corrupt file is surfaced at startup instead of showing an empty server list. Both
  take an optional data folder, and `MainViewModel` takes one too and hands it to both: without it a
  test that goes near either file reads and rewrites the real one belonging to whoever ran it.
- **`AtomicDownload`** / **`AtomicTextFile`** — the same guarantee for the other two kinds of write.
  A download lands in `<dest>.part` and is verified there, so an interrupted one can never replace a
  working file with half of one; a config file the app owns is written only when it actually changed,
  and never half-written.
- **`SingleInstance`** — one running copy per user, with a second launch bringing the first one to
  the front. A correctness guard, not a nicety: two copies each start their own server processes and
  wake listeners, and two JVMs on one world folder is how worlds get corrupted. A lock file answers
  "is anyone else running?" (the OS releases it even on a hard kill, unlike a PID file) and a named
  pipe carries the "come to the front" nudge.
- **`ServerNameRule`** / **`BukkitPathRule`** — a server's folder name, checked before it becomes a
  server that will not start: what Windows forbids outright (illegal characters, reserved device
  names, a trailing dot or space) and, separately, the two characters Paper and Purpur refuse to run
  from — including when they are in a folder *above* the server's own, which is not ours to rename.
- **`LoaderPaths`** — where each loader leaves the files that have to be found again (the version
  directory and the args file Forge/NeoForge are launched through). In one place because the
  installer, the launcher and the detector all need it, and a loader missing from one of them
  installs perfectly and then cannot be started.
- **`VerifiedJarDownload`** — announce the size, download atomically, verify, say it is done. Shared
  by Paper and Purpur, which differ only in the hash algorithm they publish.
- **`FileHashCache`** — a file's SHA-1, remembered while the file is unchanged (the key is path +
  size + write time). The Mods tab hashes every jar twice per click otherwise, once for the update
  check and once to see what is already installed.
- **`ContentMigrationService`** — what happens to installed content when a server changes family:
  the old `mods/` or `plugins/` folder is moved aside rather than left to be loaded by something
  that cannot read it.
- **`MultiVersionService`** — ViaVersion *and* ViaBackwards, which are not interchangeable: the first
  admits clients newer than the server, the second older ones, and installing only one looks like the
  feature is broken for half the people who try it. Plugin servers only, and deliberately independent
  of crossplay.
- **`DesktopShortcutService`** — the "Add to desktop" button. Three different things by platform (a
  `.lnk`, a `.desktop` entry that has to be executable and GNOME-trusted, a symlink to the bundle),
  all pointing at whatever this copy is really running from rather than a guessed install path.
- **`WindowBehavior`** — what minimize and close do, as chosen in Settings. App-wide state applied
  without a restart, the same shape as `NotificationPreferences.Global` and `ConsolePreferences`.
- **`BrowserLauncher`** — the one way a link is opened. Only absolute http(s) URLs get through,
  because in the store the URL comes from a mod author rather than from us.
- **`MarkdownParser`** / **`MarkdownBody`** — a deliberately partial Markdown reader for the long
  descriptions Modrinth returns, and the behavior that turns its blocks into controls.
- **`MinecraftRange`** — whether a Minecraft version satisfies the range a mod declares.

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
periodically by `ServerViewModel` via `GetTunnelAsync`, matching by local port **and
protocol**: a crossplay server has two tunnels, so every lookup goes through
`PlayitApiService.Match`, the one definition of "the same tunnel". The tunnel list is shared between
servers behind a 25-second cache so that N servers polling do not make N calls; the short burst of
lookups that follows creating a tunnel passes `fresh: true` to skip it, because playit takes a few
seconds to publish an address and otherwise the empty first answer would be served back to the
retries; creating a tunnel starts the same burst on the Java side, which used to wait for the
30-second timer. The address box is read-only — it shows what playit assigned, not a preference —
and the line under it is `TunnelAddressState` (`Waiting` / `NoTunnel` / `Ready` / `Failed`), the
sibling of `BedrockAddressState`: three different situations used to render as one empty box.
Compliance
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
On startup `MainViewModel.CheckForUpdatesAsync` asks `UpdateService` for the newest release and the
asset for this platform. The **Update** button (`UpdateNowCommand`) downloads it, verifies it against
the release's `SHA256SUMS.txt`, stops the servers and hands it to `SelfUpdater`.

**Every platform updates itself; only the mechanism differs.** Windows runs the silent installer,
Linux swaps the AppImage file the app is running from, and macOS mounts the `.dmg` and replaces the
`.app` bundle. What they share is the shape: nothing is touched until a complete, checksum-verified
package is on disk, and any failure leaves the current install working. Some installs cannot replace
themselves at all — an AppImage moved into `/opt` by root, a bundle in a read-only location, the app
started from `dotnet run` — and `SelfUpdater.Blocker` says so; those, and a release that ships nothing
for this platform, fall back to opening the release page.

After an update, `MainWindow.Loaded` calls `ShowWhatsNewIfUpdated`, which compares the running version
with `AppSettings.LastVersionSeen` and shows `WhatsNewDialog` (localized) with the notes from
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

### Playing from Bedrock (crossplay)
One checkbox, three things that have to line up — which is why doing it by hand goes wrong.
`CrossplayService.InstallAsync` puts **Geyser** on the server (from Modrinth, through the same
verified install path as any other mod or plugin) so it understands Bedrock clients at all, and
**Floodgate** so those players don't each have to own Minecraft: Java. Floodgate is split by source:
Modrinth carries the Fabric and NeoForge builds, and only GeyserMC's own downloads site
(`GeyserDownloadsApi`) carries the Spigot one Paper and Purpur need.

Then the **second tunnel**: Java is TCP and Bedrock is UDP, and one cannot carry the other, so
`MainViewModel` creates a UDP tunnel alongside the Java one. `CrossplayService.PickBedrockPort`
chooses the local port (19132 is only a starting point — it is taken as soon as there are two
servers), avoiding both the ports other registered servers hold and the ones the user's Playit
account already has.

Finally `GeyserConfigService` writes what Geyser cannot work out for itself: `auth-type` (a server
with Floodgate left on `online` turns every Bedrock player away), the local UDP port, and
**`broadcast-port`** — behind a tunnel the port players connect to is the tunnel's public one, and
the launcher is the only component that knows both numbers because it created the tunnel.
`RepairConfig` re-applies this when a reinstall resets the file.

How well any of it works is a property of the server type, not a promise: `ServerTypeCatalog` carries
a three-valued `CrossplayLevel` and both dialogs show the caveat before the checkbox is ticked. On
Fabric, a further checkbox installs **Hydraulic** (`HydraulicService`) so the blocks and items mods
add are converted for Bedrock clients; it is Fabric-only because Hydraulic stopped publishing
NeoForge builds in February 2026. The one failure that cannot be prevented — a NeoForge server
rejecting Geyser's mod-less connection — is at least recognised in the console and explained in the
user's language by `CrossplayDiagnostics`.

### Sleeping when empty, waking when someone joins
Two halves, both per server and both off by default.

**Sleeping** is `ServerViewModel.CheckIdleShutdown`, run off the same tick that refreshes the player
list: once a running server has had nobody on it for `ServerConfig.IdleShutdownMinutes` it stops
itself, announcing it in the console and as a notification. The window shows a live countdown
(`IdleCountdownText`), and a server that has just been woken gets a grace period so it can never be
stopped before anyone has had time to get in.

**Waking** is `WakeOnDemandListener`, which takes over the server's port while the server is stopped
and speaks the small, stable part of the Minecraft protocol needed to be honest about it: the
handshake, the server-list status (so the list shows *"Off · join to start it"* with the real icon
and player cap) and the login disconnect (so whoever presses Join gets a message while it boots).
**Pressing Join is what wakes it**, not being pinged — the client re-pings every few seconds while
the multiplayer screen is open, so waking on a status request would start the server over and over
for people who are not even playing. With a Playit tunnel this socket is reachable from the internet,
so everything it reads is treated as hostile: bounded lengths, a deadline per connection, and a cap
on how many there are at once.

### The store: search, tags and plain language
`ServerModsViewModel` asks `ModrinthService` for results already filtered by the server's loader and
game version — a result the server cannot run is worse than no result, because it installs and then
the server does not come up. Each hit is converted to a `StoreItem`, the source-independent shape the
rest of the store works on, and then two things Modrinth does not provide are added:

- **`StoreTagService`** turns it into the app's own tags. Modrinth's categories are coarse (nearly
  half the top server mods are filed under "utility") and they don't answer the question a server
  owner cares most about — whether players have to install it too — so categories, keyword rules and
  the client/server side are combined through `Resources/store-tags.json`.
- **`StoreSummaryService`** answers "what does this do to my server?" in the user's language, from a
  hand-written catalogue (`Resources/store-summaries.json`, generated by
  `tools/generate-store-summaries.py`) with a fallback sentence built from what Modrinth does say.
  It is a local lookup: no request, no key, and it works offline.

Both files can be overridden by a copy in the user's data folder, so tags and summaries can change
without a new build. Opening a result shows `ModDetailsViewModel` — gallery, versions, dependencies,
links and related projects — painted in two passes, so what the search result already carried appears
at once and the rest arrives as its requests come back. `StoreCache` (memory, then disk, then the
network) and `ImageCache` are what make going back and opening something again cost nothing, and what
make a project already seen open with no connection.

### Changing a server's type
`InstallLoaderDialog` converts an existing server in place, **keeping the world**: Vanilla into a
loader or a plugin server, one loader into another, or any of them back to Vanilla. It offers the
same list as the create dialog (the shared `ServerTypePicker`) and installs through the same
`ServerJarInstaller`, so the two cannot drift apart — they used to, and a type present in one and
missing from the other silently produced a Vanilla server. The warning above the button is keyed on
the *direction* of the change, because the directions are not equally safe: gaining a loader is
additive, while dropping to Vanilla or crossing between families is not. Content that the new family
cannot read is moved aside by `ContentMigrationService` rather than left to fail at load.

The conversion writes into the `ServerConfig` the app is already showing, and **nothing then asks
for a refresh**. The config announces each field it changed, `ServerConfigEffects` says what that
field costs, and the badge, the version, the Mods tab, its filter chips, the installed list — the
old family's folder has just been moved aside — and the store search all follow on their own. Even
cancelling the edit dialog afterwards is covered, because restoring the snapshot announces its own
assignments.

It used to rebuild the whole view model instead, and only when the *type* had changed and the server
was stopped. Converting Fabric 1.21.1 to Fabric 1.21.4 therefore changed nothing on screen, and the
browser went on offering mods picked for a version the server no longer ran — which is what made an
install fail minutes later, far from the conversion that caused it. A blanket refresh on closing the
dialog was the first fix; it is gone too, because leaving it would mean the app never exercised the
mechanism that replaced it.

### Handing the mod list to your players
`ServerModsViewModel.ExportModpack` is the file picker and nothing else; `BuildModpackAsync` takes a
path, so the part worth testing runs without a window. `ModpackWriter` builds the zip entry by entry,
reading each jar where it already sits — it used to assemble a copy of the whole pack under `%TEMP%`
first — and writing each text file with its own line endings. The pack carries the jars and a
localized instructions file naming the server, its type, its Minecraft version and the steps for that
type. It is the answer to "what do I send my friends so they can join?", which otherwise means
explaining a loader install over chat.

**What the pack leaves out.** A modded server usually carries jars a player has no use for — Geyser
and Floodgate for Bedrock crossplay, a permissions plugin, a world-backup mod — which are megabytes
downloaded and copied into a mods folder for nothing. `ExportSelection` decides, from two sources
that are each insufficient alone: what the jar declares (`ContentManifest.ContentSide`, read from
`fabric.mod.json`'s `environment` or `mods.toml`'s `side`) and Modrinth's `client_side`. The rule is
asymmetric and fits in a sentence: **a jar leaves the pack when either source says it is server-only
and neither says a client needs it.** A jar declaring itself client-side can never be dropped.
Declaring *both* counts for nothing, and finding that out cost a real modpack: the first folder this
was tried on had eleven mods and all eleven wrote `environment: "*"`, Floodgate among them — a
Bedrock authentication plugin with nothing to do on a client. It is what the template writes, so
reading it as an author's claim left the feature unable to exclude anything at all. Three rules sit
above the table — metadata claiming a
project runs on neither side is ignored as broken, anything a kept jar depends on is put back
transitively, and an export is never emptied. The store gets four seconds and then the pack is built
on the jars alone, which excludes strictly less; the notice says so. Nothing in `ExportSelection`
touches the network, and a test checks that against the file.

Afterwards a dismissable notice under the installed list names what was left out, with one button
that rebuilds the same zip including everything. Deliberately after the fact: a dialog beforehand
would charge a click to the normal case in order to serve the rare one, and the instructions inside
the pack carry the same note, because the pack has to explain itself to whoever receives it. The
button is hidden on plugin servers, where a pack is a zip a player can do nothing with.

**The scripts in the pack.** Beside the jars go `install-mods-windows.bat` and
`install-mods-unix.sh`, so receiving a pack is not four manual steps, the third of which is where
somebody's mods get deleted. `InstallScriptBuilder` fills a template held as an `EmbeddedResource`
(`Resources/scripts/*.in`) with messages from the .resx files: the shape is code and the wording is
translated, so each sentence inherits the parity checks and the logic inherits none of them. Every
placeholder becomes a whole finished message — nothing is ever a format string — so no translation
can smuggle in a shell metacharacter, and `ShellSafe`/`BatchSafe` escaping backs that up.

The scripts **move aside and never delete anything of the player's** — the one file they remove is
the loader installer they downloaded themselves, see below. Existing jars go to a `mods-backup-<stamp>` folder
beside the mods folder (a sibling, so the loader does not rescan it), a failed move stops everything
rather than leaving a half-install, and the timestamp is worked out at export time because `%DATE%`
in a .bat comes out in the machine's local format — which in much of Europe puts slashes in a folder
name. They **list the folders that actually exist** and let the player pick one: finding `.minecraft`
proves the official launcher was installed once, never that it is the one about to be used, so every
Prism, MultiMC, CurseForge and Modrinth App instance found is offered by its own name. The `.sh`
draws that list as an arrow-key menu; batch cannot read arrow keys at all — that needs PowerShell,
which mail providers block just as hard and which trips over the execution policy — so the `.bat`
uses `choice.exe`: one keypress, no Enter, every option on screen. Both colour their output, the
`.bat` only on console builds new enough to act on the escape codes rather than print them. Every
`choice` carries a timeout and a default, because batch has no way to ask whether anything is
listening and would otherwise wait for a keypress that cannot come. `MCSL_MODS_DIR` overrides the
whole thing, and the `.sh` never asks without a terminal — which is what keeps it from hanging under
a pipe, and what makes it testable.

**Installing the loader.** When the profile is missing, the script offers to install it rather than
only naming a website. `ClientLoaderInstall` resolves the installer and its published hash at export
time from the loader's own maven, so the script has no version to work out: Fabric through its
`client` CLI, Forge and NeoForge through `--installClient`. It finds Java on the `PATH` or, failing
that, inside Minecraft's own `runtime` folder, which is where the official launcher keeps a JRE that
most players do not know they have. **The download is verified against that hash before it is ever
handed to Java**, and deleted on a mismatch — the one thing these scripts are allowed to delete, and
a test states the rule that precisely. If the installer cannot be resolved when the pack is built,
the plan is simply absent and the script falls back to warning, which is a worse pack rather than a
broken one.
`InstallScriptSmokeTests` runs the real script over a planted `old.jar` and checks it still exists
afterwards. The execute bit is set but nothing depends on it: the documented invocation is
`bash install-mods-unix.sh`, which needs no bit and sidesteps macOS quarantine.

### One running copy
`Program.Main` acquires `SingleInstance` before anything else. If another copy already holds the
lock, this one nudges it to the front through the named pipe and exits without ever creating a
window. That is a correctness guard rather than tidiness: a second copy starts its own server
processes, wake listeners and idle timers, shows a server as stopped while the first has it running,
and pressing Start then means two JVMs writing one world folder.

### Checking mods/plugins for updates
`ServerModsViewModel` asks `ModrinthService` to identify each installed file on Modrinth and flag the
ones with a newer version; the user updates each with one click (checksum-verified download via
`DownloadVerifier`, preserving its enabled/disabled state).

The same scan answers a second question off the same hashes: which **library mods are missing**.
`GetVersionsByHashAsync` says what each jar *is* (project id and declared dependencies, unlike the update
endpoint, which says what could replace it), `ModDependencyService` works out what is required and absent, and
the panel offers to install it. Installing a mod resolves its dependencies the same way, in the same click —
which is the fix for a Fabric loader refusing to start over a `fabric-api` nobody was ever asked to install.

Three details that each hid a bug for a long time, and each has a test now:

- **Hashes go to Modrinth in lower case**, through `ModrinthService.ApiHashes`. The app computes
  them in upper case and Modrinth matches them literally, answering `{}` with a healthy `200 OK`; until
  1.12.3 the update check had never found an update and the missing-library panel had never shown.
- **"Everything is up to date" means the store said so.** `GetLatestVersionsByHashAsync` returns `null`
  when Modrinth could not be asked, and the tab then says it could not check — instead of passing a
  failure off as good news, which is what it used to do.
- **What is already in the folder includes what is inside its jars.** `ModIdsProvidedIn` counts the
  modules nested in `fabric-api` and the libraries other mods carry, and skips disabled jars, so the
  scan and the install never offer a second copy of something already loaded.

## Localization

All user-facing text lives in `Resources/Strings.resx` (Spanish, the neutral/base language) plus
satellite files `Strings.en.resx`, `Strings.pt.resx`, `Strings.fr.resx`, `Strings.de.resx`. Code
reads them with `Localizer.Get("Key")` (and `string.Format` for parameters); XAML uses the
`{loc:Loc Key}` markup extension. The active language comes from `AppSettings.Language` and is
applied in `App.OnFrameworkInitializationCompleted` before any window is created, so changing the
language requires a restart. See [Contributing](contributing.md) for how to add a language or a new string.
