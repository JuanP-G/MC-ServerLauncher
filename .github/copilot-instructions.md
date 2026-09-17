# Copilot / AI assistant instructions — MC Server Launcher

Conventions for this repo (a cross-platform **Avalonia / .NET 9** desktop app, MVVM). Follow them
when suggesting or writing code.

## Architecture
- **MVVM, strictly layered:** logic goes in `Services/` (UI-free, small focused classes), bindable
  state/commands in `ViewModels/` (`ObservableObject`/`RelayCommand`, no Avalonia controls), and only
  thin code-behind in `Views/`. Models are plain data in `Models/`.
- Views are **Avalonia XAML** (`.axaml` + `.axaml.cs`). Dialogs are plain `Window`s — there is no
  `ui:FluentWindow` (that was the old WPF-UI stack; the app migrated off WPF).
- Value converters live in `ViewModels/`; there is no `Converters/` folder. Attached behaviors are in
  `Behaviors/`, custom controls in `Controls/`, and the styles more than one view needs in
  `Styles/Shared.axaml` (included from `App.axaml`) rather than inside a view's own `<Window.Styles>`.
- **Colour decisions are split in two**, and new ones follow the same shape: the data (hex strings)
  in a UI-free class under `Services/`, the Avalonia brushes next to it in `ViewModels/` —
  `ConsoleColors`/`ConsolePalette`, `NotificationPalette`/`NotificationBrushes`,
  `ServerTypeCatalog`/`ServerTypeBrushes`. A colour that ends up in `settings.json` must not need a
  type that knows Avalonia exists.

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
**`.editorconfig` at the repo root is the source of truth** — formatting and naming are applied by the
IDE and by `dotnet format McServerLauncher.sln`. Suggest code that already matches it. The rules worth
repeating, plus the ones a tool can't check:

- **Naming:** `PascalCase` for types/methods/properties; `_camelCase` for private *instance* fields;
  `PascalCase` with **no** underscore for private `static`/`const` fields; `camelCase` for locals and
  parameters. The underscore is what distinguishes per-instance state at a glance, which is exactly
  why a `static` field doesn't carry one.
- **Async methods end in `Async`** — except `[RelayCommand]` methods, which are named after the button
  (`private async Task Start()` → `StartCommand`).
- **Declaring state:** collaborators first, `private readonly X _x = new();`. Bindable state is
  `[ObservableProperty] private T _foo;`, never a hand-written `OnPropertyChanged` pair. Derived values
  are expression-bodied properties. **Don't use primary constructors** — that field block is where the
  reader learns what a class is made of.
- **`ServerConfig` and `NotificationSettings` are observable** (`ObservableObject`), because a
  dialog edits the very instance the view models are showing. Add properties to them the normal way,
  `[ObservableProperty] private T _foo;`. It does not change `servers.json` —
  `ServerConfigFormatTests` proves it. `AppSettings` stays plain: its dialog edits a copy.
- **A new field on `ServerConfig` needs a row in `ViewModels/ServerConfigEffects.cs`**: the
  view-model properties it feeds, the work to redo (rescan the content folder, re-search the store,
  re-read the port…), or a written reason nothing shows it. `ServerViewModel` subscribes to
  `Config.PropertyChanged` and applies the row; `ServerConfigEffectsTests` fails until the row
  exists. Do not add another hand-written refresh method — that is what this replaced.
- **A constructor assembles; `Activate()` starts.** Timers, sockets, shared-singleton subscriptions
  and network calls never go in a constructor. They go in `Activate()`, mirrored by
  `ShutdownAsync()`, both safe to call twice (`ServerViewModel`, `MainViewModel`). It is what makes
  them testable, and `MainWindow` calls `MainViewModel.Activate()` from `Loaded`.
- **Comments and identifiers are in English** in `McServerLauncher/` (`.axaml` included),
  `McServerLauncher.Tests/` and `.github/workflows/`. (User-facing text is localized, per above.)
  The maintainer's own scripts — `publish.ps1`, `installer/`, `tools/`, `web/_i18n/` — are the one
  documented exception and stay in Spanish; match the file you are in.
- Comments say **why**, not what: record the decision and what went wrong without it. Wrap `///` and
  prose at ~100 columns, code at ~110.
- No absolute machine paths — use `Environment.GetFolderPath(...)`; per-user data lives under
  `%APPDATA%\McServerLauncher\`.
- Public types get an XML `///` summary, and so does any member whose contract isn't obvious from its
  name (it powers the DocFX API reference).
- `.gitattributes` settles line endings; never hand-convert a file's line endings in a change.

## Commits, PRs and issues
**Conventional Commits, written in Spanish.** `tipo(ámbito): asunto` — lower case, no full stop,
~70 chars. The subject describes the **problem or the result, not the mechanism**: the diff already
says which lines moved.

- `fix(bedrock): el puerto que no aparecía, y los túneles que se pisaban` ✅
- `fix(bedrock): cambiar PickBedrockPort para usar accountTunnels` ❌

Types: `feat` · `fix` · `seg` (security — its own type, because "what changed about that since the
last audit?" has to be answerable from `git log --grep '^seg'`) · `perf` · `refactor` · `docs` ·
`test` · `ci` · `chore` · `release` (always `release: X.Y.Z`, no scope).

Scopes are the Spanish feature area, one spelling per concept; the canonical list is in
`docs/articles/contributing.md`. **Reuse one before inventing one.**

Body: Spanish, 80 columns, on anything non-trivial — what happened before and why it was a real
problem, what it does now, why this way and what was rejected. `.gitmessage` is the template
(`git config commit.template .gitmessage`).

A **PR title is a commit subject**, and its description is that commit's body. Issue titles come
pre-filled in the same shape by the forms in `.github/ISSUE_TEMPLATE/`. Security never goes in a
public issue — see `SECURITY.md`.

## Documentation is part of the change
Anything that alters behaviour alters the docs in the same commit, and **both languages every time**:
- feature → `README.md` + `README.es.md`
- service / flow / folder → `docs/articles/architecture.md` + `architecture.es.md`
- convention or workflow → `docs/articles/contributing.md` + `contributing.es.md` + this file
- user-facing string → all five `.resx` (a test enforces it)

## Security / correctness expectations
- **Verify downloads** against official checksums via the shared `DownloadVerifier` (delete the file
  on mismatch); most sources publish SHA-1/256/512. Two documented exceptions, both explained in the
  service that owns them: Fabric's meta endpoint publishes no checksum at all (the jar is validated
  structurally instead), and Purpur publishes only MD5 (HTTPS authenticates the source; the hash is
  there to catch a corrupted download, and nothing relies on it for more).
- **Secrets are encrypted at rest** (`SecretProtector`: DPAPI on Windows, AES-GCM elsewhere).
- **Sanitize external input** before it reaches a process or a config file (CR/LF in player names for
  stdin, values written to `server.properties`).

## Tests
- `McServerLauncher.Tests/` is xUnit and references the app directly; the logic worth testing is
  `internal` and reaches it via `InternalsVisibleTo`. Run with
  `dotnet test McServerLauncher.Tests/McServerLauncher.Tests.csproj`.
- CI runs them on **every branch** and on **Windows and Linux both** — several exercise real sockets,
  named pipes and file locks, which .NET implements differently per platform. A test that is only
  meaningful on one platform skips itself on the other.
- Tests needing real Avalonia controls go through the shared headless `AvaloniaFixture`.

## Releases
See `docs/articles/contributing.md` → "Release a new version" for the full checklist (bump the version
in csproj + `.iss`, add the `Changelog` entry + `Whatsnew_x_y_z` keys, run `publish.ps1`, attach the
`.exe` **and** `SHA256SUMS.txt`). Don't skip the changelog or the checksum file.
