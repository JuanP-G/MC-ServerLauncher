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

Run the tests:

```powershell
dotnet test McServerLauncher.Tests/McServerLauncher.Tests.csproj
```

Build the documentation site locally (this page):

```powershell
dotnet tool install -g docfx   # first time only
.\docs\build-docs.ps1          # builds and serves at http://localhost:8080
```

## Tests

`McServerLauncher.Tests/` is an **xUnit** project that references the app directly — the decision
logic worth testing is `internal` on purpose (it is implementation, not API) and reaches the tests
through `InternalsVisibleTo`, so renaming a member breaks the build instead of failing at run time
the way reflection would.

`.github/workflows/tests.yml` runs them **on every branch**, on **Windows and Linux both**. That is
deliberate: several of them exercise real sockets, named pipes and file locks, and .NET implements
those differently on each platform (a named pipe is a Unix domain socket on Linux). A test that only
makes sense on one platform should skip itself on the other rather than assert something different.

Tests that need real Avalonia controls use `AvaloniaFixture`, a single headless application shared by
the whole run — Avalonia can only be initialized once per process, and its controls must be touched
from the thread that initialized it. It exists because a bug shipped twice that nothing else could
catch: the type picker reporting the *previous* selection inside its own change event.

They come in six layers, and it is worth knowing which one a new test belongs to:

| Layer | Example | What it can see |
|---|---|---|
| The models on their own | `ServerConfigFormatTests`, `ModelNotificationTests` | What is written to disk; which properties announce |
| The table | `ServerConfigEffectsTests` | That no field of the config was left undeclared |
| View models, built for real | `ServerViewModelRefreshTests`, `MainViewModelFlowTests` | That a change to the config reaches the card, the panels and `servers.json` |
| Real controls | `AddEditServerDialogTests`, `CreateServerDialogTests` | That it reaches the screen — the half a unit test cannot see |
| Gates on the source | `StartDependencyGateTests`, `StoreHashTests`, `ExportSelectionTests` | That a promise holds in the file itself — no network, one definition |
| The artefact, run | `InstallScriptSmokeTests` | That the script a player gets actually works — run with bash, on Linux in CI |

`ServerViewModel` and `MainViewModel` take a data folder and start nothing until `Activate()`, so a
test can own one without touching `%APPDATA%`, Playit or the network. Tests never call `Activate()`.
`ServerModsView` cannot be rendered headless (the icon font), so its bindings are checked by reading
the `.axaml` and reflecting against the view model — see `MissingDependencyPanelTests`.

## Code style

The whole repository is written in one style, and most of it is **applied for you**: `.editorconfig`
at the root holds the formatting and naming rules, and Visual Studio, Rider and VS Code read it
without any setup. Before opening a pull request:

```powershell
dotnet format McServerLauncher.sln
```

That leaves nothing to argue about in review. `.gitattributes` settles line endings the same way —
the repository stores LF and hands your working copy whatever your platform wants — so a Windows and
a Linux contributor never produce a diff where every line changed and nothing did.

The pull-request template repeats the checklist, so nothing here has to be remembered at the moment
it matters. The rules below are the ones worth knowing by heart, and the few a tool cannot check.

### Naming

| Kind | Style | Example |
|---|---|---|
| Types, methods, properties, events | `PascalCase` | `ServerProcessManager`, `EnsureJavaAsync` |
| Private instance fields | `_camelCase` | `private readonly ServerConfig _config;` |
| Private **static** fields and `const` | `PascalCase`, no underscore | `private static readonly HttpClient Http` |
| Locals and parameters | `camelCase` | `var levelName = …` |
| Interfaces / type parameters | `IPascalCase` / `TPascalCase` | `IProgress<string>`, `TResult` |

The underscore is the point of the first two rows: it is how you tell per-instance state from
everything else without scrolling, which is also why a `static` field does **not** get one.

**Async methods end in `Async`.** The one exception is a `[RelayCommand]` method, which is named
after the button (`private async Task Start()` → `StartCommand`); the toolkit generates the command
from that name, and `StartAsyncCommand` reads worse everywhere it is bound.

Names describe *what the thing is for*, not what it is made of: `CrossplayService`, not
`GeyserHelper`. A name that needs a comment to be understood is the comment's fault half the time and
the name's fault the other half — prefer fixing the name.

### Declaring state

- A class opens with its collaborators, `private readonly` and initialised inline:
  `private readonly ModrinthService _modrinth = new();`. That block is where the reader finds out
  what the class is made of, which is why **primary constructors are not used** here — they hide it
  behind a parameter list.
- Bindable state is `[ObservableProperty] private string _searchQuery = string.Empty;` and nothing
  else. Never a hand-written `OnPropertyChanged` pair.
- Anything derived is an expression-bodied property: `public bool UpdateAvailable => Update is not null;`
- **A persisted model that a dialog edits while it is on screen is observable.** `ServerConfig` and
  `NotificationSettings` derive from `ObservableObject`, and a property added to either goes in as
  `[ObservableProperty] private T _foo;` like anywhere else. It does not change what is written to
  disk, and `ServerConfigFormatTests` is there to prove that stays true. `AppSettings` is the
  exception and stays plain: its dialog edits a copy and commits it on OK.
- **A new field on `ServerConfig` needs a row in `ServerConfigEffects`** — which view-model
  properties it feeds and what has to be redone, or a line saying why nothing on screen derives from
  it. `ServerConfigEffectsTests` fails until it is there, and the failure says what to write. This
  is not bureaucracy: a field left out shows the right answer until somebody edits that server, and
  the wrong one from then until the app is restarted.
- `var` when the type is already on the line (`var dialog = new SettingsDialog(…)`), the type spelled
  out when it is not.
- **A constructor assembles; `Activate()` starts.** Nothing that polls, opens a socket, subscribes to
  a shared singleton or goes to the network belongs in a constructor — it goes in `Activate()`, whose
  mirror is `ShutdownAsync()`, and both must be safe to call twice. `ServerViewModel` and
  `MainViewModel` are split this way, which is the only reason either can be built in a test.

### Layering

- Keep the **MVVM** split: logic in `Services/` (UI-free), bindable state and commands in
  `ViewModels/`, only thin code-behind in `Views/`, plain data in `Models/`.
- A colour decision is split in two: the hex strings in a UI-free class under `Services/`, the
  Avalonia brushes beside it in `ViewModels/` — `ConsoleColors`/`ConsolePalette`,
  `NotificationPalette`/`NotificationBrushes`, `ServerTypeCatalog`/`ServerTypeBrushes`. A colour that
  ends up in `settings.json` must not need a type that knows Avalonia exists.
- Styles that more than one view needs live in `Styles/Shared.axaml`, not in a view's own
  `<Window.Styles>`. **Give a shared style a name that means one thing:** two views once had a
  `Border.card` each, meaning two different things, and Avalonia kept them apart only because they
  were local — merging them would have silently restyled a view with nothing failing.
- **No absolute machine paths.** Use `Environment.GetFolderPath(...)`; per-user data lives under
  `%APPDATA%\McServerLauncher\`.

### Comments and documentation

- **Comments and identifiers are in English**, everywhere a contributor reads code:
  `McServerLauncher/` (including `.axaml`), `McServerLauncher.Tests/` and `.github/workflows/`.
  User-facing text is **not** hard-coded — it goes through the localization system (see below).
  The maintainer's own release and website scripts (`publish.ps1`, `installer/`, `tools/`,
  `web/_i18n/`) are still written in Spanish, comments and console output alike; they are the one
  documented exception, not a place to copy the habit from.
- Public types get an XML `///` summary, and so does any member whose contract is not obvious from
  its name. That is what powers the API Reference.
- A comment says **why**, not what. The ones worth writing here are the ones that record a decision
  and what happened without it — "Purpur publishes only an MD5, and here is why that is enough" is
  worth a paragraph; "// loop over the mods" is worth deleting.
- Wrap `///` blocks and prose at about **100 columns**, code at about **110**.

### Correctness rules learnt the hard way

Each of these is here because breaking it shipped a bug that no test caught at the time.

- **A failure must never read as good news.** When a lookup cannot be made, return `null` or a
  distinct state — never the empty result that also means "nothing to do". The update check said
  "everything is up to date" with no connection, and Modrinth answered upper-case hashes with an
  empty `200 OK`, so "Check for updates" never found anything for as long as it existed. Nobody
  reports good news. `UpdateCheckTests` pins the return type by reflection for exactly this reason.
- **One question, one definition.** When two places answer the same question, route both through
  one and add a test that fails if a second copy appears. It has drifted three times: tunnel
  matching (`PlayitApiService.Match`), the hashes sent to the store (`ModrinthService.ApiHashes`)
  and the dependencies a jar declares (`ContentManifest`). Each time only one copy learnt what was
  needed.
- **A test for a fix has to fail against the old code.** Check it — stash the fix, run the test,
  watch it go red. A test that passes either way proves nothing, and it is easy to write one.
- **Read what the loader reads.** Mods carry other mods inside themselves (`META-INF/jars/`,
  `META-INF/jarjar/`), and the loaders bundle libraries of their own (`mixinextras`). Anything that
  decides what is installed has to see both, or it reports as missing what the server loads fine.
- **Embedded resources with dotted names need `WithCulture=false` and a `LogicalName`.** MSBuild
  infers a culture from the segments of a file name, `sh` is a real one, and
  `install-mods-unix.sh.in` silently went to a satellite assembly while the build stayed green.
- **State that must survive a rebuild cannot live in what gets rebuilt.** The Mods tab rebuilds its
  rows from disk after every update, toggle and delete, and the updates a check found lived on the
  rows — so updating one mod made the buttons on all the others vanish. Keep such state in the view
  model and hand it back to each new row.
- **A JVM option the app adds is gated by the Java version.** An option the JVM does not recognise
  stops it from starting at all, which is worse than any warning it was meant to silence. See
  `ServerProcessManager.ImpliedJvmFlags`, and never override what the user put in their extra arguments.
- **Scripts written for players.** Logic in a `.in` template, every visible line in the .resx files,
  and a placeholder always replaced by a whole finished message — never a format string. Line
  endings are per file (`.sh` LF, `.bat` CRLF), no BOM anywhere, and every `choice` in a `.bat`
  carries `/t` and `/d`, because batch cannot tell whether anyone is there to press a key.

## Commits, pull requests and issues

Everything written about a change follows one shape, so that `git log` reads as a history of
decisions rather than a list of edits. It is **Conventional Commits, in Spanish**:

```
tipo(ámbito): qué cambia, contado como se lo contarías a alguien

Qué pasaba antes, y por qué era un problema de verdad. Qué hace ahora. Por qué
así y no de la otra forma, con lo que se descartó.

Closes #12
```

Run this once and git will open that shape for you, with the rules as comments:

```powershell
git config commit.template .gitmessage
```

### The subject line

In Spanish, lower case, no full stop, about **70 characters** (90 is the ceiling). It describes the
**problem or the result, not the mechanism** — the diff already says which lines moved:

| | |
|---|---|
| ✅ | `fix(bedrock): el puerto que no aparecía, y los túneles que se pisaban` |
| ❌ | `fix(bedrock): cambiar PickBedrockPort para usar accountTunnels` |

Two related things join with `, y`. If they will not fit in one sentence, they are two commits.

### Types

| Type | For |
|---|---|
| `feat` | A new thing the user can do |
| `fix` | Behaviour that was wrong |
| `seg` | Security: verifying, sanitising, bounding, encrypting |
| `perf` | Faster or cheaper, same behaviour |
| `refactor` | Same behaviour, better shape |
| `docs` | Documentation — the `.md` files, the `///` comments, the website |
| `test` | Tests, and only tests |
| `ci` | GitHub Actions workflows and packaging |
| `chore` | Dependencies, tooling, tidying |
| `release` | Always `release: X.Y.Z`, no scope |

`seg` is deliberately its own type rather than a flavour of `fix`. This app downloads and executes
code on the user's machine, so "what have we changed about that since the last audit?" is a question
somebody asks periodically — and it has to be answerable from `git log --grep '^seg'` rather than by
reading four hundred commits.

### Scopes

The feature area, in Spanish, optional. Use one that already exists before inventing one, and use
**one spelling per concept** — the history has both `notif` and `notificaciones`, which is exactly
what this list is for:

`consola` · `mods` · `tienda` · `jugadores` · `servidor` · `tipos` · `crossplay` · `bedrock` ·
`playit` · `java` · `paper` · `forge` · `fabric` · `puertos` · `backups` · `avisos` · `colores` ·
`actualizaciones` · `ajustes` · `nombres` · `seguridad` · `datos` · `ui` · `web` · `tests` · `ci` ·
`release`

Adding one that will recur? Add it to this list in the same commit.

### The body

In Spanish, wrapped at **80 columns**, and expected on anything that is not trivial. Write what a
`git blame` two years from now needs and cannot get from the diff: what the old behaviour was, why
it was a real problem, and what you rejected on the way. The existing history is the reference —
`git show bf6453a` is a good one to read before writing your first.

Footers are `Closes #12`, `Refs #12`, or `BREAKING CHANGE:` with what breaks and what to do about it.

### Pull requests

- **The title is a commit subject** — same types, same scopes, same Spanish. `.github/pull_request_template.md`
  fills in the rest, including the checklist.
- The description is the body of the commit you would have written for the whole branch.
- One idea per pull request. The merge commit is
  `Merge pull request #N: <título corto> (versión si aplica)`.

### Issues

Opening one goes through a form (`.github/ISSUE_TEMPLATE/`), which pre-fills the title in the same
shape: a bug starts as `fix(ámbito): `, a proposal as `feat(ámbito): `. **Write in English or
Spanish, whichever you prefer** — both get read.

A **security problem is never a public issue.** [`SECURITY.md`](https://github.com/JuanP-G/MC-ServerLauncher/blob/main/SECURITY.md)
explains the private channel and lists the trade-offs that are already known, so you can tell a
finding from a documented decision.

## Documentation is part of the change

This repository treats the documentation as part of the change, not as follow-up work:

- A new or changed **feature** → both `README.md` and `README.es.md`.
- A new **service, flow or folder** → both `docs/articles/architecture.md` and `architecture.es.md`.
- A new **convention or workflow** → this file and `contributing.es.md`, and
  `.github/copilot-instructions.md` so AI assistants suggest the same thing a reviewer would ask for.
- A new **user-facing string** → all five `.resx` files (a test enforces this one).

The English and Spanish pages are the same document twice. Updating one and not the other is how a
reader ends up trusting a page that is quietly a version behind.

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

### Add a server type
1. One row in `Services/ServerTypeCatalog.cs`: display name, family (plugins / mods / neither), badge
   colour and its `CrossplayLevel`. The picker, the badges, the store, the content folder and the
   crossplay rules all read from that table.
2. One branch in `Services/ServerJarInstaller.cs`, which is the single place that knows how each type
   is obtained. The create dialog and the change-type dialog both call it.
3. Add the type's description key to the five `.resx` files (the line under its name in the picker).
4. **Never renumber the `ServerType` enum.** `servers.json` stores it as an integer, so moving a
   member reinterprets every server already saved on every machine. New types go on the end.

> Resist adding a `switch` on `ServerType` anywhere else. Six of them is what the catalogue replaced,
> and a type present in five of the six looked right and behaved as something else.

### Add a new dialog or service
- **Dialog:** create `Views/MyDialog.axaml` + `.axaml.cs` as a plain Avalonia `Window`, localize its
  texts with `{loc:Loc ...}`, and open it from a ViewModel command (see `WhatsNewDialog` or
  `PlayitApiKeyDialog` as small examples).
- **Service:** add a focused class in `Services/`, keep it UI-free, and inject/instantiate it from
  the relevant ViewModel (see how `ServerViewModel` composes its services).

### Release a new version
1. Bump `<Version>` in `McServerLauncher/McServerLauncher.csproj` — that's the **single source of
   truth**. `publish.ps1` reads it and passes it to Inno Setup. Keep the `.iss` fallback
   `MyAppVersion` aligned only so a direct/manual Inno Setup build doesn't produce a stale filename.
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
> **Before changing how versions are numbered, check what the installed version can parse.** A
> release whose tag has a shape older clients do not understand is invisible to them, however
> correct the new code is — and the code that understands it ships *inside* that release. It has
> happened twice: pre-releases were invisible before 1.10.1, and four-number tags before 1.10.3.1,
> because in both cases `UpdateService` had to change to see them. The first release of a new shape
> always has to be handed over by hand; say so instead of promising an automatic update.

> **Numbering.** A beta carries a fourth number that extends the stable it follows: after `1.10.3`
> come `1.10.3.1`, `1.10.3.2`, and the finished work ships as the next stable (`1.11.0`). It is that
> way round on purpose — numbering betas after the version they lead *to* would make the stable sort
> below its own betas, stranding everyone who tested them.

5. **For a beta**, publish it as a pre-release from its branch instead:
   ```powershell
   gh release create vX.Y.Z --prerelease --target <branch> dist/MC-ServerLauncher-Setup-X.Y.Z.exe dist/SHA256SUMS.txt
   ```
   GitHub leaves pre-releases out of `/releases/latest`, so people on the stable line are never pushed onto one.
   From 1.10.1 the updater reads the release *list* instead, which is what makes a beta reachable at all — and it
   says it is a beta before the Update button is pressed. Anything older than 1.10.1 cannot see betas at all, so
   a first beta after a stable has to be handed over by hand.
6. Publishing the release automatically triggers the **Linux** (`release-linux.yml`) and **macOS**
   (`release-macos.yml`) workflows, which build and attach the `.AppImage` and the two `.dmg`s. Don't
   upload those by hand — just wait for the workflows to finish.

## Documentation site (GitHub Pages)

The API Reference + these articles are published automatically to GitHub Pages by
`.github/workflows/docs.yml` on every push to `main`. The site is generated with **DocFX** from the
`///` comments and the markdown in `docs/`.

> **One-time setup by the repo owner:** enable Pages in **Settings → Pages → Source: “GitHub
> Actions”**. After that, every push updates the site automatically.
