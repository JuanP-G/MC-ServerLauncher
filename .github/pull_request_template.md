<!--
WRITE THE TITLE AND THE DESCRIPTION OF THIS PULL REQUEST IN ENGLISH. Always, and no matter what
language the issue, the commits or the conversation were in. Anyone who reads the project should
understand a pull request; this applies to people and to AI assistants alike.
(Commit messages stay in Spanish: the history keeps one voice. Only the pull request is English.)

TITLE: same shape as a commit — type(scope): what changes, lower case, in English.
  feat(console): colours by category, filters with a counter and highlighting
  fix(bedrock): the port that did not show up, and the tunnels that overwrote each other
Types: feat · fix · seg · perf · refactor · docs · test · ci · chore · release
Scope: the feature area in English (console, mods, store, players, server, types, crossplay,
bedrock, playit, java, paper, forge, fabric, ports, backups, notifications, colours, updates, news,
settings, names, security, data, ui, web, tests, ci, wake, memory, network, performance). Reuse one before inventing one.
Full guide: docs/articles/contributing.md ("Pull requests").
-->

## What this changes

<!--
Three paragraphs at most, in the same order as the body of a commit:
  1. What happened before, and why it was a real problem.
  2. What it does now.
  3. Why this way — and what was rejected.
If it fixes an issue: "Closes #123".
-->

## How you tested it

<!-- What you ran and on which platform. If it cannot be tested automatically, say so. -->

## Checklist

- [ ] **The title and this description are in English**
- [ ] `dotnet format McServerLauncher.sln` run, so the diff is the change and not the formatting
- [ ] `dotnet test McServerLauncher.Tests/McServerLauncher.Tests.csproj` passes
- [ ] Naming follows the house style: `_camelCase` private instance fields, `PascalCase` private
      statics and constants, `Async` suffix (except `[RelayCommand]` methods)
- [ ] MVVM split held: no UI types in `Services/`, no logic in `Views/`
- [ ] Any new user-facing text is a key in **all five** `.resx` files, never hard-coded
- [ ] Public types carry an XML `///` summary
- [ ] Commits follow `tipo(ámbito): asunto`, and each one is one idea

### Documentation moved with the code, in both languages

- [ ] New or changed feature → `README.md` **and** `README.es.md`
- [ ] New service, flow or folder → `docs/articles/architecture.md` **and** `architecture.es.md`
- [ ] New convention or workflow → `docs/articles/contributing.md`, `contributing.es.md`,
      `.github/copilot-instructions.md` **and** `CLAUDE.md`
- [ ] Nothing above applies — this changes no behaviour anyone documents

<!--
If you ticked nothing in that last block and the change does alter behaviour, it is not ready:
a page that is quietly a version behind is worse than no page.

Is it a security problem? Do not open it here: SECURITY.md explains the private channel.
-->
