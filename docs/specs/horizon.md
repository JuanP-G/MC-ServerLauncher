# Horizon as a server type: specification and plan

Status: draft, 2026-11-21. No code written. Based on `origin/main` (c5fd6a2). Spanish version:
[horizon.es.md](horizon.es.md) (keep both in step).

> **On hold (2026-11-28).** `github.com/horizon-rust` (repository and releases) has returned 404 since about
> 25 November, so there is no official download source for the nightly this plan pins. The website is still up and
> its links point at the missing repository. The cause is not confirmed: GitHub's DMCA log (last entry
> 25 November) has no notice naming Horizon, and one comment on Hacker News says an automated bot flagged the
> organisation and the team has appealed. **No code phase starts until an official source is back.** Forks and
> third-party mirrors are not an option: their binaries cannot be verified, and at least one removes the game
> ownership requirement. Forks held by major contributors still seem to be up, and the removal may well be a
> mistake, but neither is an official source. Resume when `github.com/horizon-rust` is back. Section 2 describes Horizon as it was before this.

## 1. What Horizon is and why it fits

Horizon (`horizon-rust/horizon`) is a rewrite of Minecraft Java 26.3 in Rust. Its server, `horizon-server`, is
one native program: no Java, one settings file (`server.toml`), a local web panel and backups. Vanilla 26.3
clients join unchanged.

Why it matters to the launcher: hosting a server on very low-end computers.

| Figure (source: the project itself) | Value |
|---|---|
| RAM with 1 / 50 players | 0.7 GB / 1.1 GB (Java: 3.4 to 4.5 GB) |
| Start | 8 to 9 s (Java: 15 to 22 s) |
| Idle CPU | 2 % of one core (example from its docs) |
| Ticks | 20 TPS up to 50 bots; 9 to 11 TPS at 100 bots; single-threaded main tick |

Caveats this sheet must respect:

- These are **the project's own** measurements. No independent benchmarks. We measure it ourselves (phase 0).
- Paper's average tick is **shorter** at low player counts. Horizon wins on memory, not speed.
- Minimum hardware is **not documented**. The figures come from a 4-thread i7-7500U.
- No Java plugins or mods. Its mods are native and run unsandboxed.
- No native Bedrock; Geyser/Floodgate are mentioned only for the whitelist bypass file.
- Minecraft 26.3 only. `accept_older_clients` is work in progress.
- Licence: **LGPL-3.0** (confirmed in phase 0; its website still says MIT).
- Unofficial, not affiliated with Mojang or Microsoft. Very active (789 open issues): it moves fast.

## 2. Technical facts that shape the design

### 2.1 Distribution
- The stable release `v0.2.97` (2026-10-28) calls itself **Outdated**. The current server is the `nightly`
  release (prerelease, tag `nightly`, rebuilt from `main` every few hours).
- Official installers (`install.sh`, `install.ps1`) download the nightly and check an Ed25519 signature
  (OpenSSL 3) or GitHub's SHA-256. They install `horizon-server` and `horizon-prepare`.
- Platforms: Linux x86-64 and macOS agree in every source; Windows x64 works (see 2.6).

### 2.2 Game files (the hard part)
Horizon ships nothing from Mojang. It needs "game files" prepared with
`horizon-prepare --prepare --server-jar PATH --client-jar PATH`, which requires **Java 25**, the 26.3
`server.jar`, the 26.3 `client.jar`, the libraries and the assets (see 2.6). Output goes to `game-files/`.
A headless machine can receive a prepared folder with `horizon-server import-resources DIR`.

### 2.3 First run and configuration
- `horizon-server init` (interactive) or with flags:
  `init --eula --world NAME --seed N --port 25565 --whitelist --player X --op X --yes` (`--offline` sets
  `online_mode=false`). Only an explicit yes writes `eula=true`.
- `server.toml` is the source of truth; the supervisor writes vanilla `server.properties` before each start.
  Unknown keys are **errors** (a typo prevents starting).
- `horizon-server config --json` and `config set KEY=VALUE`.
- Vanilla formats are kept: `ops.json`, `whitelist.json`, `banned-*.json`, `usercache.json`, `eula.txt`,
  `world/`, `logs/latest.log`. Extra: `whitelist-bypass.txt` (Floodgate UUID prefix).

### 2.4 Running and control
- Foreground: `horizon-server run` (stdin console; Ctrl-C or SIGTERM stop cleanly).
- Background: `start | stop | restart | status [--json]`, output in `logs/supervisor.log`.
- `status --json`: phase, TPS, MSPT, players, chunks, RAM, CPU.
- One-off: `cmd ...`; `players --json`; `whitelist`, `op`, `ban`, `kick`, `backup`, `restore`.
- Control API: free port on 127.0.0.1, address and token in `.horizon/api.json`.
- Web panel: `127.0.0.1:8130`, one-time login link (`horizon-server panel`).
- Crash handling: restart after 5 s with backoff, gives up after 5 crashes in 10 min.

### 2.5 Backups and updates
- `backups/` zips (world, vanilla files, `server.toml`); automatic every 6 h, 12 kept.
- `update --check --json`, `update` (download, verify, stop, back up, switch, start, wait up to 5 min,
  auto-rollback on failure) and `rollback`.

## 2.6 Phase 0 results (2026-11-21, nightly `0a875da`, Windows x64)

Tested in an isolated folder, only on 127.0.0.1:25599, each download hash-checked.

| Question | Result |
|---|---|
| Server for Windows | **Yes, but not as a server archive.** The nightly publishes only `horizon-server-linux-x86_64.tar.gz`. On Windows use `horizon-windows-x64.zip` (77.5 MB, 212 MB unpacked) with `horizon-server.exe`, `horizon-prepare.exe` and `horizon-client.exe`. There are also `horizon-windows-arm64.zip` and `horizon-macos-arm64.zip`. |
| Signature | `SHA256SUMS`, `SHA256SUMS.sig` and `SHA256SUMS.v2.sig` (Ed25519) on the release. GitHub's published SHA-256 matched. |
| Licence | **LGPL-3.0** confirmed (`LICENSE` file and GitHub API). |
| Is `server.jar` enough? | **No.** `horizon-prepare` also needs the 26.3 **client.jar** (41.5 MB), the libraries and the assets (index 34, 487 MB per Mojang). Without them it fails in 0.2 s with "Missing ...26.3.jar". |
| Preparation | `horizon-prepare --prepare --server-jar X --client-jar Y --progress json` finished in **35 s** with Java 25. It leaves 276 MB of prepared files plus 248 MB of working files in `.local`. In this test it read libraries and assets from the user's `.minecraft` (read-only). `--check` still asks for `horizon-prepare sounds` (optional sounds). |
| Progress | `--progress json` emits `{"fraction": 0.0-1.0}` and `{"message": "..."}` lines: usable as is for the app's progress bar. |
| Ready line | **Vanilla format**: `[HH:MM:SS] [Server thread/INFO]: Done (119.379s)! For help, type "help"`. Command answers are prefixed `System chat: ` (as in Minecraft 26.3). |
| Start | **119 s**, not the 8 to 9 s of its docs. 82 s were "resources loaded" (items: 35 s). First start, WebAssembly cache disabled on Windows ("owner/DACL not verified"), 12 threads. We do not know whether later starts are faster. |
| Idle memory | **364 MB** RSS with 0 players and 0 chunks (peak 365 MB); 20 TPS, average MSPT 0.7 to 1.0 ms, CPU 0 to 1.5 %. `low_memory = true` promises about 60 MB less. |
| Status | `status --json` works: `phase` (`starting`, then `running`), `sample.rss_bytes`, `cpu_percent`, `game.tps`, `mspt`, `players`, `levels`. A supervisor and an "engine" process (`horizon-client.exe` in server mode) run: the app must count the RAM of both. |
| Stop | `stop` on stdin (and Ctrl-C) saves and exits with code 0 in under 1 s. |
| Server folder | After `init` only `eula.txt` and `server.toml`; `run` creates the rest. 1.3 MB with no explored world. |
| `init --eula` | With `--no-whitelist --offline --yes` it asks nothing. The app must pass `--eula` only after the user ticks a box. |

Consequences for the design:

1. **Preparation goes from "server jar + Java" to "client + server + libraries + assets + Java 25".**
   Automating it means the app **downloads Mojang's client**, which is not the same as downloading the server
   jar for Vanilla (decided: see risk 10).
2. **Windows is no longer a blocker**, but the "server" is a 212 MB package with the client inside.
3. **"Very low-end" is unproven**: 364 MB idle is a good figure, but the 119 s start and about 520 MB of
   disk for preparation must be measured on a modest machine before promising anything.
4. **Ready signal**: use the `Done (...)` line as for Vanilla, with `status --json` (`phase: running`) as a
   fallback.

## 3. Decisions taken

| Topic | Decision |
|---|---|
| Game files | **The app automates it**: downloads what is missing, uses an app-managed Java 25 and runs `horizon-prepare`. The user only sees progress. |
| Download channel | **Nightly pinned by hash**: downloaded, signature/SHA-256 verified, and the build recorded. |
| v1 scope | **Full**: `server.toml` editor, backups with `backup`/`restore`, update with `rollback`, TPS/RAM status. |
| Mojang client | **Both ways (2026-11-21)**: use a complete 26.3 from the official launcher if present; otherwise download from Mojang with the SHA-1 from the version JSON. |
| Deliverable | This sheet and its phased plan, no code. |

Inherited from Pumpkin (not re-decided): type is **Experimental** with a warning; `Native = true`; mandatory
hash (`requireChecksum`); restricted download hosts (`TrustedHosts.GitHubReleaseAssets`).

## 4. Risks and open questions

1. **Mojang and the jars.** Show the EULA before preparing. The game files are **never published or
   redistributed**.
2. **Managed Java 25.** `JavaService` already detects and downloads runtimes. Check whether it offers 25 and
   whether the runtime can be used only for preparing. The JDK costs disk and is used once: consider deleting
   it after preparing.
3. **Nightly as a channel.** It changes every few hours and can break. Mitigation: pin the tested build as
   "last known good", update only with notice, back up first (its `update` does).
4. **Windows.** No server archive; use the full zip (see 2.6). Verify ARM64.
5. **Ready and console.** Use the `Done` line, with `status --json` as a backup.
6. **Collision with app-managed files.** `server.toml` regenerates `server.properties`: the app must not edit
   `server.properties` on a Horizon server (`ServerPropertiesService` already tells native types apart).
7. **Ports.** 25565 TCP (goes into the Firewall and Playit notices), panel 8130 and API on 127.0.0.1: never
   exposed by the app. Bedrock wake does not apply.
8. **Licence.** LGPL-3.0: check what linking or redistributing the zip implies before bundling anything.
9. **Real hardware.** Measure RAM and CPU on a modest machine before promising "low-end" in the docs.
10. **Mojang client.** Decided: both ways (see section 3). The app downloads into its own folder; nothing is
    written to the user's `.minecraft`.
11. **119 s start.** If repeated on later starts, the ready wait needs a generous timeout and a progress
    message; the app's wake mode and idle shutdown would stop being practical with Horizon.

## 5. Design in the app (follows the Pumpkin pattern)

New files (provisional names):

- `Services/HorizonService.cs`: picks the release, verifies and downloads (like `PumpkinService`).
- `Services/HorizonPrepareService.cs`: orchestrates Mojang files, Java 25, `horizon-prepare` and progress;
  retries and cleanup on failure; detects already prepared game files.
- `Services/HorizonConfig.cs`: reads and writes `server.toml` (typed model; unknown keys are errors), using
  `config --json` / `config set` where possible so we do not reimplement its validation.
- `Services/HorizonControl.cs`: wrapper for `status --json`, `players --json`, `cmd`, `backup(s)`, `restore`,
  `update --check --json`, `rollback`, with `--dir`.
- `Services/HorizonConsole.cs`: console output parsing, like `PumpkinConsole`.
- Resources: `TypeLogos/horizon.png`, new keys in the 5 resx files (neutral is Spanish).
- Tests: `HorizonConfigTests`, `HorizonServiceTests`, `HorizonPrepareTests`, `HorizonConsoleTests`,
  `HorizonRealServerTests` (env vars `MCSL_HORIZON_EXE` and optional download, like `PumpkinRealServerTests`).

Changes to existing code: new `ServerType.Horizon` value at the end of the enum (a file format, never
reordered); a `ServerTypeCatalog` row (`Native: true, Experimental: true`, family `None`, crossplay `None`);
`ServerProvisioningService`, `ServerProcessManager`, `StartPreflight`, `ServerDetectionService`,
`ServerDiskUsage`, `CommandCatalog`, `CrossplayService`, `PlayerFiles`, `ServerViewModel`, `NativeConfigCard`
and `ServerSettingsPages`: wherever there is a Pumpkin branch, decide what Horizon needs.

## 6. Phased plan (one PR per phase, English; commits in Spanish)

Every PR updates docs in both languages. Suggested scope: `horizon`.

**Phase 0 — Spike (done, see 2.6).** Still open: a second start, a modest machine, a real 26.3 client,
Windows ARM64.

**Phase 1 — Type and download.** Enum, catalogue, logo, texts (5 resx), `HorizonService` with verification,
picker with the Experimental badge. Done when: a folder with the verified server can be created and the
`HorizonServiceTests` pass (asset per platform, hash, disallowed host, release without signature).

**Phase 2 — Game file preparation.** `HorizonPrepareService`, managed Java 25, Mojang client download or reuse
of the official launcher's, progress, translated errors, EULA shown first. Done when: from zero and without
Java a server ends up prepared; a failure leaves the folder clean.

**Phase 3 — Start, console and status.** Native process, `init` with flags from the wizard, ready signal,
console, clean stop, whitelist and ops via vanilla files, Firewall and Playit, TPS/RAM on the card. Done when:
create, start, join with a 26.3 client, stop. Tested with a real client.

**Phase 4 — Configuration.** `server.toml` editor in the server detail (no pop-ups, project rule), with
"applies now" or "after restart" marks; invalid-key warning before saving.

**Phase 5 — Backups and updates.** Backups tab (automatic, manual, restore with "Replace the world"
confirmation), check and apply update with rollback and a recorded known-good build.

**Phase 6 — Wrap-up.** README and architecture (ES/EN), web (`python web/_i18n/build.py`),
`docs/articles/contributing*`, architecture Mermaid, a signed-feed news item if relevant, audit register entry.

Order: 0 → 1 → 2 → 3 sequential; 4 and 5 are independent of each other after 3.

## 7. Tests

- Unit: asset choice, tag/version parsing, hash, hosts, `HorizonConfig` (round trip, unknown keys),
  `HorizonConsole`, preparation orchestration with fake processes.
- Real server (optional, env vars): start, `status --json`, `cmd list`, stop.
- Manual before merge: real window, real 26.3 client, modest machine, Windows with UAC for the Firewall.
- Avalonia verification with the screenshot harness (compiling proves nothing with `CompileBindings=False`).

## 8. Out of scope (for now)

Horizon's native mods, proxies (Velocity/transfer), Horizon's Discord integration, RCON/GS4/management
protocol, Bedrock, 26.2 clients, Linux ARM (built from source), Horizon's Windows service (the app already
manages startup), Horizon's client and launcher.

## 9. Sources

- https://github.com/horizon-rust/horizon (README, `docs/hosting.md`, `docs/server.md`)
- https://playhorizon.dev/docs/hosting and https://playhorizon.dev/hub
- https://api.github.com/repos/horizon-rust/horizon/releases/tags/nightly (nightly 2026-11-20)

Not fully read: the second half of `hosting.md` and `server.md`, `docs/api/server-reference.md`,
`install.sh`/`install.ps1`, `docs/telemetry.md`.
