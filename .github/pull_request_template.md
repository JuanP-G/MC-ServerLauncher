<!--
TÍTULO: la misma forma que un commit — tipo(ámbito): qué cambia, en español y en minúscula.
  feat(consola): colores por categoría, filtros con contador y resaltado
  fix(bedrock): el puerto que no aparecía, y los túneles que se pisaban
Tipos: feat · fix · seg · perf · refactor · docs · test · ci · chore · release
Guía completa: docs/articles/contributing.md — Full guide: same file, in English below it.
-->

## Qué cambia · What this changes

<!--
Tres párrafos como mucho, en el mismo orden que el cuerpo de un commit:
  1. Qué pasaba antes y por qué era un problema de verdad.
  2. Qué hace ahora.
  3. Por qué así — y qué se descartó.
Si arregla un issue: "Closes #123".
-->

## Cómo lo has probado · How you tested it

<!-- Qué ejecutaste y en qué plataforma. Si no se puede probar automáticamente, dilo. -->

## Checklist

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
- [ ] New convention or workflow → `docs/articles/contributing.md`, `contributing.es.md` **and**
      `.github/copilot-instructions.md`
- [ ] Nothing above applies — this changes no behaviour anyone documents

<!--
If you ticked nothing in that last block and the change does alter behaviour, it is not ready:
a page that is quietly a version behind is worse than no page.

¿Es un problema de seguridad? No lo abras aquí: SECURITY.md explica el canal privado.
-->
