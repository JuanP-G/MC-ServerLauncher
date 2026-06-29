# Contributing

> 🇪🇸 ¿Prefieres español? Lee la versión en [español](contributing.es.md).

Thanks for wanting to help! This guide covers how to build the project and how to make the most
common changes.

## Requirements

- **Windows** 10/11 (the UI uses WPF, which is Windows-only).
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
1. Add the control + label/description in `Views/ServerConfigDialog.xaml` (and bind it in its
   code-behind).
2. Read/write the key through `ServerPropertiesService.Read` / `Update`, which preserves the rest of
   the file, comments and order.

### Add a new dialog or service
- **Dialog:** create `Views/MyDialog.xaml` + `.xaml.cs` as a `ui:FluentWindow`, localize its texts
  with `{loc:Loc ...}`, and open it from a ViewModel command (see `WhatsNewDialog` or
  `PlayitApiKeyDialog` as small examples).
- **Service:** add a focused class in `Services/`, keep it UI-free, and inject/instantiate it from
  the relevant ViewModel (see how `ServerViewModel` composes its services).

### Release a new version
1. Bump `<Version>` in `McServerLauncher/McServerLauncher.csproj` **and** `MyAppVersion` in
   `installer/MC-ServerLauncher.iss`.
2. Run `publish.ps1` (self-contained publish + Inno Setup installer in `dist/`).
3. `gh release create vX.Y.Z dist/MC-ServerLauncher-Setup-X.Y.Z.exe` with **bilingual** notes.
4. The in-app updater looks for the `.exe` asset in the latest release, so always attach it.

## Documentation site (GitHub Pages)

The API Reference + these articles are published automatically to GitHub Pages by
`.github/workflows/docs.yml` on every push to `main`. The site is generated with **DocFX** from the
`///` comments and the markdown in `docs/`.

> **One-time setup by the repo owner:** enable Pages in **Settings → Pages → Source: “GitHub
> Actions”**. After that, every push updates the site automatically.
