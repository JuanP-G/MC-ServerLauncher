# Copilot / AI assistant instructions — MC Server Launcher

Conventions for this repo (a cross-platform **Avalonia / .NET 9** desktop app, MVVM). Follow them
when suggesting or writing code.

## Architecture
- **MVVM, strictly layered:** logic goes in `Services/` (UI-free, small focused classes), bindable
  state/commands in `ViewModels/` (`ObservableObject`/`RelayCommand`, no Avalonia controls), and only
  thin code-behind in `Views/`. Models are plain data in `Models/`.
- Views are **Avalonia XAML** (`.axaml` + `.axaml.cs`). Dialogs are plain `Window`s — there is no
  `ui:FluentWindow` (that was the old WPF-UI stack; the app migrated off WPF).
- The single value converter (`BoolOpacityConverter`) lives in `ViewModels/`; there is no
  `Converters/` folder. Attached behaviors are in `Behaviors/`, custom controls in `Controls/`.

## Localization (important)
- **No user-facing text is hard-coded.** Every string goes through the localization system.
- Add a key to **all five** `.resx` files in `Resources/`: the neutral `Strings.resx` holds the
  **Spanish** text; `Strings.en/pt/fr/de.resx` hold the translations.
- Read strings with `Localizer.Get("Key")` (and `string.Format(...)` for `{0}` placeholders) in code,
  or `{loc:Loc Key}` in XAML.

## Adding a server type
- One row in `Services/ServerTypeCatalog.cs` (name, family, badge colour, crossplay) plus a branch in
  `Services/ServerJarInstaller.cs`. The picker, the badges, the mod store, the content folder and the
  crossplay rules all read from the catalogue — do not add another `switch` on `ServerType`.
- **Never renumber the `ServerType` enum.** `servers.json` stores it as an integer, so moving a member
  reinterprets every server already saved on every machine. New types go on the end.

## Code style
- **Comments and identifiers are in English.** (User-facing text is localized, per above.)
- No absolute machine paths — use `Environment.GetFolderPath(...)`; per-user data lives under
  `%APPDATA%\McServerLauncher\`.
- Public types/members get an XML `///` summary (it powers the DocFX API reference).

## Security / correctness expectations
- **Verify downloads** against official checksums via the shared `DownloadVerifier` (delete the file
  on mismatch); most sources publish SHA-1/256/512. Two documented exceptions, both explained in the
  service that owns them: Fabric's meta endpoint publishes no checksum at all (the jar is validated
  structurally instead), and Purpur publishes only MD5 (HTTPS authenticates the source; the hash is
  there to catch a corrupted download, and nothing relies on it for more).
- **Secrets are encrypted at rest** (`SecretProtector`: DPAPI on Windows, AES-GCM elsewhere).
- **Sanitize external input** before it reaches a process or a config file (CR/LF in player names for
  stdin, values written to `server.properties`).

## Releases
See `docs/articles/contributing.md` → "Release a new version" for the full checklist (bump the version
in csproj + `.iss`, add the `Changelog` entry + `Whatsnew_x_y_z` keys, run `publish.ps1`, attach the
`.exe` **and** `SHA256SUMS.txt`). Don't skip the changelog or the checksum file.
