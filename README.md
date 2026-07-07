# 🎮 MC Server Launcher

**🇬🇧 English · 🇪🇸 [Español](README.es.md)**

Desktop app for **Windows** (and Linux) to manage one or several **Minecraft** servers from a modern
graphical interface — **no `.bat` files, black console windows or editing config files by hand**.

Create a server, pick the **type** (Vanilla, Fabric, Forge or Paper) and version, add **mods or plugins**
with a couple of clicks, open it to the Internet with Playit.gg, manage players and tweak the settings…
all with buttons.

> Built with **Avalonia / .NET 9** — cross-platform, Fluent design, dark theme.

## 📸 A look inside

**Server list, live console and stats — each server tagged with its type**

![Main view](docs/screenshots/main.png)

**Mods & plugins browser — searches Modrinth, already filtered by your server's type and version**

![Mods and plugins browser](docs/screenshots/mods-plugins.png)

**Player management (whitelist, operators, bans…)**

![Player management](docs/screenshots/players.png)

**Visual `server.properties` editor — every setting explained in plain language**

![Visual settings editor](docs/screenshots/settings.png)

## ⬇️ Download & install

1. Go to the **[latest release](https://github.com/JuanP-G/MC-ServerLauncher/releases/latest)**.
2. Download **`MC-ServerLauncher-Setup-x.y.z.exe`** and run it (creates a Desktop + Start-menu shortcut).
3. Open the app and create or add your server. **You don't need to install .NET or Java** — the app handles it.
4. Updates happen **inside the app**: when a new version exists, a banner offers an **Update** button, and a
   **What's new** window tells you what changed.

> The first time, Windows SmartScreen may warn (new, unsigned app): click *More info → Run anyway*.
>
> **macOS / Linux:** grab the `.dmg` (macOS) or `.AppImage` (Linux) from the same release. On macOS the app
> isn't Apple-signed yet, so the first time **right-click the app → Open** to get past Gatekeeper.

## ✨ Features

- **Multiple servers** at once, each with its own config and a **type badge** (Vanilla / Fabric / Forge / Paper).
- **Create a server** automatically: pick the **type**, **version** (official Mojang list), **port** and **RAM**;
  the app downloads the right server, accepts the EULA, prepares `run.bat` / `server.properties`, and installs
  the correct **Java** (Temurin) if needed. Vanilla/Fabric/Forge use **mods**; Paper uses **plugins**.
- **Mods & plugins browser** 🧩 — search **Modrinth** inside the app, already **filtered by your server's type
  and version** (with type + version chips so it's obvious). Sort by relevance or downloads, one-click
  **Install**, and **enable/disable** or delete installed items. Paper servers browse plugins; the rest, mods.
- **Change a server's type** — turn an existing server into Fabric/Forge/Paper or back to Vanilla, **keeping the
  world**, with clear colour-coded warnings about what each change can affect.
- **Start / Stop / Restart** with a clean stop that saves the world; detects and frees a **busy port**; live
  **CPU, RAM, uptime and port** with colour status.
- **Minecraft-style view** — server icon, coloured MOTD, `players/max` and a reachability signal.
- **Real-time console** with copyable text, a command box and a **command-help** panel.
- **Players** 👥 — connected (live), operators, whitelist, banned and known players, with OP / kick / ban /
  whitelist actions.
- **Visual `server.properties` editor** with plain-language explanations.
- **Share to the Internet with Playit.gg** 🌐 — connect your account by pasting a one-time **setup code**
  (no keys, no files). The app **creates the tunnel and runs the Playit agent for you**, so your server is
  reachable from anywhere and friends join with the public address — **you install nothing**. The app ships
  no secret of its own (the credential lives in a small proxy).
- **Notifications** 🔔 — optional pop-ups when a player joins or leaves, someone dies (PvP), the server
  crashes, or auto-restart gives up. Configurable per type, globally and **per server**, with a test button.
- **Settings in one place** ⚙️ — language, notifications and your Playit connection in a single dialog.
- **Multi-language** — English, Spanish, Portuguese, French and German.

## 🛠️ Build from source

```powershell
git clone https://github.com/JuanP-G/MC-ServerLauncher.git
cd MC-ServerLauncher
dotnet run --project McServerLauncher

# Self-contained build (users install nothing):
dotnet publish McServerLauncher -c Release -r win-x64 --self-contained
```

## 💻 Platform support

| Platform | Works? |
|---|---|
| Windows x64 | ✅ Yes (installer `.exe`) |
| Windows ARM64 | ✅ Yes, via x64 emulation (no native build yet) |
| Linux x64 | ✅ Yes (AppImage) |
| macOS (Apple Silicon & Intel) | ✅ Yes (DMG) |

> The published Windows installer is **x64 only** (Inno Setup `ArchitecturesAllowed=x64compatible`);
> there's no separate x86 or native ARM64 build.

## 📖 Docs & data

Developer documentation (architecture, contributing guide and a full **API reference**) is published with
**DocFX** at **https://juanp-g.github.io/MC-ServerLauncher/**. Per-user data lives under
`%APPDATA%\McServerLauncher\`: `servers.json`, `settings.json`, the installed `java\`, the persistent
console `logs\` (kept 14 days) and, on Linux/macOS, `.secret.key`. Each server's own folder also
keeps a `backups\` directory with the automatic world backups.
