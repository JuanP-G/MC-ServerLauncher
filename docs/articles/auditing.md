# Code audits

> 🇪🇸 ¿Prefieres español? Lee la versión en [español](auditing.es.md).

Every so often the code that went in since the last audit is read again from top to bottom: once
for bugs a person can see, once for bugs nobody sees until they cost something, and once for code
that repeats itself or will be hard to extend. This page is how that is done and how it is reported,
so that two audits a year apart can be compared, and so anybody — a contributor or an AI assistant —
can run the next one the same way.

## How long since the last one

Each audit leaves an annotated tag on the commit it reviewed, `auditoria/YYYY-MM-DD`:

```powershell
git describe --tags --match 'auditoria/*'       # auditoria/2026-10-14-37-g1a2b3c4 → 37 commits since
git log --oneline auditoria/2026-10-14..main    # what the next audit has to read
git diff --stat auditoria/2026-10-14..main -- McServerLauncher McServerLauncher.Tests
```

The tag marks the code that was **read**, not the code that was fixed: the fixes come afterwards, in
their own pull requests, and the next audit reads them too. The [log](#log) below says what each one
found and where it was fixed.

## Scope

From the last `auditoria/*` tag to `main`, in three areas, with every file in an area read whole —
the diff only says which files to open:

| Area | What it covers |
|---|---|
| Settings and UI | The server settings page, the new-server page, number boxes, the views' bindings |
| Content | Mods and plugins: install, update, replace, cleanup, the store, Crossplay/Hydraulic/MultiVersion |
| Lifecycle | Start, stop, backups and restore, wake-on-demand, the connection test, the app's own plumbing |

And across all of them: the `.resx` keys, the text an error puts on screen, and the house style.

## What to look for

- **Internal text on screen.** An exception message (`ex.Message`), a .NET type name, English inside a
  Spanish sentence, a resource key instead of its text, an unfilled `{0}`, `NaN`. The user has already
  seen `InvalidCastException: Could not convert '(null)' to System.Int32` under a number box once.
- **Bindings that fail in silence.** The views use `x:CompileBindings="False"`: a property name with
  the wrong casing compiles and simply shows nothing. Check every path against its data context.
- **The five `.resx` files.** Every key used exists in all of them, with the same placeholders, and
  keys nothing uses any more are removed.
- **State that does not come back.** An `Is*` flag, a gate or a half-copied config left set when an
  exception takes another path.
- **Writes that can be left half done.** A file the app relies on is written to a temporary name and
  moved into place; anything listed to the user (a backup zip) only appears once complete.
- **Threads.** Collections changed off the UI thread, `async void`, fire-and-forget tasks whose
  failures nobody sees, cancellation that is accepted and then ignored.
- **Windows and Linux.** Case-sensitive paths, file locks that exist on one and not the other, tests
  that return early off Windows and so prove nothing on the other CI runner.
- **Two answers to one question.** New code that re-implements a helper that already exists (name it
  and where it is), or the same logic three times with three slightly different results.
- **The house style.** `.editorconfig`, the naming rules, 110 columns, comments in English.

## Severity

| Severity | Meaning |
|---|---|
| **High** | Data is lost or damaged, a server cannot start, or the UI says something about the server that is not true |
| **Medium** | Wrong behaviour a user can meet in normal use, or internal text on screen |
| **Low** | An edge case, a misleading message, a small cost nobody would notice |
| **Design** | Duplication or a shape that makes the next change harder — no wrong behaviour today |
| **Style** | Breaks the house style |

## Verifying before reporting

Every finding is marked **confirmed** — the code path was read end to end, or the failure was
reproduced — or **plausible** — it depends on a timing, an outside service or a framework detail
nobody checked yet. Anything reported as high is verified before it is reported, and checking can
move a finding down: in the 2026-10-14 audit, "updating EssentialsX deletes its modules" was checked
against the Modrinth API, which publishes one file per version, and went from high to low.

A finding that needs the UI to be sure is reproduced before it is fixed, in a throwaway Avalonia app
that hosts the real view: it logs binding errors (`LogToTrace(LogEventLevel.Warning)`) and takes a
screenshot with `RenderTargetBitmap`. Compiling proves nothing about a view whose bindings are not
compiled.

## The report

The report is written for the person who decides what gets fixed, so each finding is phrased as what
a user would see or lose, not as what the code does.

1. **A page with the findings** (an HTML page; the maintainer's are kept as Claude artifacts):
   - the range, the tag and the previous tag at the top;
   - one count per severity;
   - a list that filters by severity and by area, one row per finding with its severity, area,
     **confirmed / plausible**, the pull request that fixes it, a title that is the symptom, the
     concrete scenario (inputs and state → wrong result), and the `file:line` to look at.
2. **A choice of scope**: what gets fixed now and what goes to an issue.
3. **A tracking issue** titled `Auditoría YYYY-MM-DD`, with every finding as a task.
4. **A row in the [log](#log).**

## Fixing

- Pull requests **by area**, each from `main` in its own worktree, each with tests that fail against
  the old code. A cross-cutting change that the others build on (one error translator, say) goes first.
- What an audit teaches goes into
  [Correctness rules learnt the hard way](contributing.md#correctness-rules-learnt-the-hard-way), in
  the same pull request as the fix it came from.
- When the last fix is merged, the log row gets its pull requests and the issue is closed.

## Log

| Date | Tag | Range | High · Medium · Low · Design | Issue | Fixed in |
|---|---|---|---|---|---|
| 2026-10-04 | `auditoria/2026-10-04` | the whole app, up to `5c6d45e` | 40 fixes (security, reliability, performance) | — | #34, #35 |
| 2026-10-14 | `auditoria/2026-10-14` | `auditoria/2026-10-04..989678f` (v1.17.0 → 1.19.1) | 6 · 13 · 17 · 8 | pending | pending |
