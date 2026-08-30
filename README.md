# 🎮 MC Server Launcher

**🇬🇧 English · 🇪🇸 [Español](README.es.md)**

[![Website](https://img.shields.io/badge/Website-mc--server--launcher.vercel.app-3FB950?style=for-the-badge&logo=vercel&logoColor=white)](https://mc-server-launcher.vercel.app)
[![Documentation](https://img.shields.io/badge/Docs-API%20reference-1F6FEB?style=for-the-badge&logo=readthedocs&logoColor=white)](https://juanp-g.github.io/MC-ServerLauncher/docs/)
[![Download](https://img.shields.io/github/v/release/JuanP-G/MC-ServerLauncher?style=for-the-badge&label=Download&color=5CE07B)](https://github.com/JuanP-G/MC-ServerLauncher/releases/latest)
[![License](https://img.shields.io/badge/License-MIT-8B949E?style=for-the-badge)](LICENSE)

🌐 **[Website](https://mc-server-launcher.vercel.app)** (also in
[English](https://mc-server-launcher.vercel.app/en/) ·
[Deutsch](https://mc-server-launcher.vercel.app/de/) ·
[Français](https://mc-server-launcher.vercel.app/fr/) ·
[Português](https://mc-server-launcher.vercel.app/pt/)) —
📖 **[Documentation](https://juanp-g.github.io/MC-ServerLauncher/docs/)**

Desktop app for **Windows, Linux and macOS** to manage one or several **Minecraft** servers from a
modern graphical interface — **no `.bat` files, black console windows or editing config files by hand**.

Create a server, pick the **type** from a grid of cards that says which take plugins, which take mods and which
Bedrock can reach (Vanilla, Paper, Purpur, Fabric, NeoForge, Forge), pick a version, add **mods or plugins**
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
4. Updates happen **inside the app, on all three platforms**: when a new version exists a banner offers an
   **Update** button, and the app downloads it, **checks it against its published SHA-256** and installs it
   by itself — running the installer on Windows, swapping the AppImage on Linux and the app bundle on macOS.
   A **What's new** window then tells you what changed.

> The first time, Windows SmartScreen may warn (new, unsigned app): click *More info → Run anyway*.
>
> **macOS / Linux:** grab the `.dmg` (macOS) or `.AppImage` (Linux) from the same release. On macOS the app
> isn't Apple-signed yet, so the first time **right-click the app → Open** to get past Gatekeeper.

## ✨ Features

- **Multiple servers** at once, each with its own config and a **type badge** (Vanilla / Paper / Purpur / Fabric /
  NeoForge / Forge).
- **Create a server** automatically: pick the **type**, **version** (official Mojang list), **port** and **RAM**;
  the app downloads the right server, accepts the EULA, prepares `run.bat` / `server.properties`, and installs
  the correct **Java** (Temurin) if needed. Fabric, Forge and NeoForge use **mods**; Paper and Purpur use **plugins**.
- **Mods & plugins store** 🧩 — search **Modrinth** inside the app, already **filtered by your server's type
  and version** (with type + version chips so it's obvious). Every result carries a **plain-language summary of
  what it does in your language** and a warning when it also has to be installed on the client. Open a **details
  page** with the gallery, versions, dependencies, links and related mods without leaving the app. A **Filters
  panel** combines several categories at once and shows the applied ones as chips you can remove one by one.
  One-click **Install**, and **enable/disable** or delete installed items. Paper servers browse plugins; the
  rest, mods.
- **Play from other Minecraft versions** — one checkbox installs ViaVersion and ViaBackwards, so clients both newer
  and older than the server can join. Plugin servers only.
- **Change a server's type** — turn an existing server into Paper/Purpur/Fabric/Forge/NeoForge or back to Vanilla, **keeping the
  world**, with clear colour-coded warnings about what each change can affect.
- **Start / Stop / Restart** with a clean stop that saves the world; detects and frees a **busy port**; live
  **CPU, RAM, uptime and port** with colour status.
- **Minecraft-style view** — server icon, coloured MOTD, `players/max` and a reachability signal.
- **Real-time console** with copyable text, a command box and a **command-help** panel.
- **Players** 👥 — connected (live), operators, whitelist, banned and known players, with OP / kick / ban /
  whitelist actions.
- **Visual `server.properties` editor** with plain-language explanations.
- **Sleeps and wakes on its own** 💤 — a server can **stop itself after N minutes with nobody on**, and
  **start itself again when somebody tries to join**. While it sleeps the app answers on the server's port,
  so the server list shows *"Off · join to start it"* and whoever presses Join gets a message while it boots.
  The window shows a **countdown** to the shutdown, and a freshly woken server gets a grace period so it is
  never stopped before anyone can get in. Both halves are per server and **off by default**.
- **Automatic world backups** 💾 — a copy before every start and on every stop, a configurable number kept,
  plus **Back up now** and one-click **restore** from the app.
- **Stays out of the way** — optionally minimize and/or close **to the system tray** so your servers keep
  running with the window gone. Launching the app again brings that window back instead of opening a second
  copy over the same servers.
- **Share to the Internet with Playit.gg** 🌐 — connect your account by pasting a one-time **setup code**
  (no keys, no files). The app **creates the tunnel and runs the Playit agent for you**, so your server is
  reachable from anywhere and friends join with the public address — **you install nothing**. The app ships
  no secret of its own (the credential lives in a small proxy).
- **Notifications** 🔔 — optional pop-ups when a player joins or leaves, someone dies (PvP), the server
  crashes, auto-restart gives up, an **empty server stops itself**, or one **starts itself because somebody
  tried to join**. Configurable per type, globally and **per server**, with a test button.
- **Settings in one place** ⚙️ — language, notifications, tray behaviour, your Playit connection and an
  **Add to desktop** button, all in a single dialog.
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
**DocFX** at **https://juanp-g.github.io/MC-ServerLauncher/docs/**. Per-user data lives under
`%APPDATA%\McServerLauncher\` (`~/.config/McServerLauncher/` on Linux and macOS): `servers.json`,
`settings.json`, the installed `java\`, the persistent console `logs\` (kept 14 days), the `instance.lock`
that keeps the app to one running copy, and, on Linux/macOS, `.secret.key`. Each server's own folder also
keeps a `backups\` directory with the automatic world backups.

## 📄 License

Released under the **[MIT License](LICENSE)** — free to use, modify and redistribute, including
commercially, as long as the copyright notice stays. Provided as is, without warranty.

The licence covers this project's own source code — see [NOTICE](NOTICE) for third-party software.
*Minecraft* is a trademark of Mojang Studios /
Microsoft, and this project is not affiliated with or endorsed by them. The Minecraft server files,
Java runtimes, mods and the Playit.gg agent the app downloads belong to their respective owners and
keep their own licences.
