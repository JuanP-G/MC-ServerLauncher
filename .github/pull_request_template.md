<!--
Gracias por contribuir / Thanks for contributing.
Full guide: docs/articles/contributing.md — Guía completa: docs/articles/contributing.es.md
-->

## What this changes / Qué cambia

<!-- One paragraph: what it does and, above all, why. If it fixes an issue, "Closes #123". -->

## Checklist

- [ ] `dotnet format McServerLauncher.sln` run, so the diff is the change and not the formatting
- [ ] `dotnet test McServerLauncher.Tests/McServerLauncher.Tests.csproj` passes
- [ ] Naming follows the house style: `_camelCase` private instance fields, `PascalCase` private
      statics and constants, `Async` suffix (except `[RelayCommand]` methods)
- [ ] MVVM split held: no UI types in `Services/`, no logic in `Views/`
- [ ] Any new user-facing text is a key in **all five** `.resx` files, never hard-coded
- [ ] Public types carry an XML `///` summary

### Documentation moved with the code, in both languages

- [ ] New or changed feature → `README.md` **and** `README.es.md`
- [ ] New service, flow or folder → `docs/articles/architecture.md` **and** `architecture.es.md`
- [ ] New convention or workflow → `docs/articles/contributing.md`, `contributing.es.md` **and**
      `.github/copilot-instructions.md`
- [ ] Nothing above applies — this changes no behaviour anyone documents

<!--
If you ticked nothing in that last block and the change does alter behaviour, it is not ready:
a page that is quietly a version behind is worse than no page.
-->
