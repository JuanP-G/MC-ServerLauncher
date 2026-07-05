# Contributing

> 🇪🇸 ¿Prefieres español? Lee la versión en [español](contributing.es.md).

Thanks for wanting to help! This guide covers how to build the project and how to make the most
common changes.

## Requirements

- **Windows**, **Linux** or **macOS** (the UI uses Avalonia, which is cross-platform).
- **.NET 9 SDK**.
- *(Only to build the installer)* **Inno Setup 6**.

## Build and run

```powershell
git clone https://github.com/JuanP-G/MC-ServerLauncher.git
cd MC-ServerLauncher
dotnet run --project McServerLauncher
```

Build the documentation site locally (this page):

```powershell
dotnet tool install -g docfx   # first time only
.\docs\build-docs.ps1          # builds and serves at http://localhost:8080
```

## Conventions

- **Code comments and identifiers are in English.** User-facing text is **not** hard-coded — it goes
  through the localization system (see below).
- Keep the **MVVM** split: logic in `Services/`, bindable state/commands in `ViewModels/`, only thin
  code-behind in `Views/`.
- **No absolute machine paths.** Use `Environment.GetFolderPath(...)` and `%APPDATA%`.
- Public types/members get an XML `///` summary — that's what powers the API Reference.

## How-to recipes

### Add a language
1. Copy `Resources/Strings.resx` to `Resources/Strings.<code>.resx` (e.g. `Strings.it.resx`) and
   translate every `<value>`.
2. Add the code to `<SatelliteResourceLanguages>` in `McServerLauncher.csproj`.
3. Add it to the `Languages` list in `MainViewModel` so it shows in the sidebar selector.

### Add a translatable text
1. Add the same `<data name="MyKey">` entry to **all five** `.resx` files with the translation.
2. Use it from XAML as `{loc:Loc MyKey}`, or from code as `Localizer.Get("MyKey")`
   (use `string.Format(Localizer.Get("MyKey"), arg)` when it has `{0}` placeholders).

### Add a `server.properties` setting to the visual editor
1. Add the control + label/description in `Views/ServerConfigDialog.axaml` (and bind it in its
   code-behind).
2. Read/write the key through `ServerPropertiesService.Read` / `Update`, which preserves the rest of
   the file, comments and order.

### Add a new dialog or service
- **Dialog:** create `Views/MyDialog.axaml` + `.axaml.cs` as a plain Avalonia `Window`, localize its
  texts with `{loc:Loc ...}`, and open it from a ViewModel command (see `WhatsNewDialog` or
  `PlayitApiKeyDialog` as small examples).
- **Service:** add a focused class in `Services/`, keep it UI-free, and inject/instantiate it from
  the relevant ViewModel (see how `ServerViewModel` composes its services).

### Release a new version
1. Bump `<Version>` in `McServerLauncher/McServerLauncher.csproj` — that's the **single source of
   truth**. `publish.ps1` reads it and passes it to Inno Setup, so you don't touch the `.iss` (its
   `MyAppVersion` is only a fallback for building the installer by hand).
2. Add a **what's-new** entry so the update dialog has something to show:
   - A new tuple at the top of `Entries` in `Services/Changelog.cs` (newest first), e.g.
     `(new Version(1, 6, 0), "Whatsnew_1_6_0")`.
   - The matching `Whatsnew_x_y_z` key in **all five** `.resx` files (Spanish text in the neutral
     `Strings.resx`, translations in the rest). See *Add a translatable text* above.
3. Run `publish.ps1`. It publishes the self-contained `win-x64` build, builds the Inno Setup installer
   in `dist/`, **and** writes `dist/SHA256SUMS.txt` next to it.
4. Create the release with **both** assets and **bilingual** notes:
   ```powershell
   gh release create vX.Y.Z dist/MC-ServerLauncher-Setup-X.Y.Z.exe dist/SHA256SUMS.txt
   ```
   - The in-app updater looks for the `.exe` asset (so always attach it) **and** for
     `SHA256SUMS.txt`, which it uses to verify the installer before running it
     (`UpdateService.CheckAsync`). Verification is **mandatory**: without that file the updater
     refuses the silent install and just opens the release page — so never skip it.
5. Publishing the release automatically triggers the **Linux** (`release-linux.yml`) and **macOS**
   (`release-macos.yml`) workflows, which build and attach the `.AppImage` and the two `.dmg`s. Don't
   upload those by hand — just wait for the workflows to finish.

## Documentation site (GitHub Pages)

The API Reference + these articles are published automatically to GitHub Pages by
`.github/workflows/docs.yml` on every push to `main`. The site is generated with **DocFX** from the
`///` comments and the markdown in `docs/`.

> **One-time setup by the repo owner:** enable Pages in **Settings → Pages → Source: “GitHub
> Actions”**. After that, every push updates the site automatically.
