# Contributing to MC Server Launcher

**🇬🇧 English · 🇪🇸 [Español](#contribuir-a-mc-server-launcher)**

Thanks for wanting to help. The full guide — building, the test suite, the code style, how to add a
language, a server type or a release — lives in the documentation site:

📖 **[Contributing guide](https://juanp-g.github.io/MC-ServerLauncher/docs/articles/contributing.html)**
 · source: [`docs/articles/contributing.md`](docs/articles/contributing.md)
🏗️ **[Architecture](https://juanp-g.github.io/MC-ServerLauncher/docs/articles/architecture.html)**
 · source: [`docs/articles/architecture.md`](docs/articles/architecture.md)

## The short version

```powershell
git clone https://github.com/JuanP-G/MC-ServerLauncher.git
cd MC-ServerLauncher
dotnet run --project McServerLauncher                                  # run it
dotnet test McServerLauncher.Tests/McServerLauncher.Tests.csproj       # test it
dotnet format McServerLauncher.sln                                     # match the house style
```

Five things a reviewer will always check:

1. **The style is applied, not argued about.** `.editorconfig` holds it and `dotnet format` applies
   it. Private instance fields are `_camelCase`, private statics and constants are `PascalCase` with
   no underscore, async methods end in `Async` (except `[RelayCommand]` ones, named after the button).
2. **The MVVM split holds.** Logic in `Services/` with no UI in it, bindable state and commands in
   `ViewModels/`, thin code-behind in `Views/`, plain data in `Models/`.
3. **No user-facing text is hard-coded.** Every string is a key in **all five** `.resx` files —
   Spanish in the neutral `Strings.resx`, the translations beside it. A test fails if one is missing.
4. **The tests pass on Windows *and* Linux.** CI runs both on every branch, and several tests use
   real sockets, pipes and file locks, which behave differently on each.
5. **The documentation moved with the code**, in **both languages**: README for a feature,
   `architecture.md` for a service or a flow, `contributing.md` for a convention.

Comments and identifiers are in English; only what the user reads is translated.

## Writing it down

Commits are **Conventional Commits in Spanish** — `tipo(ámbito): asunto`, describing the problem
rather than the mechanism, with a body that says what happened before and why this way. Turn on the
template once and it will guide you:

```powershell
git config commit.template .gitmessage
```

A pull request title is a commit subject; issue titles come pre-filled in the same shape. Full rules,
the list of types and the canonical scopes:
**[Commits, pull requests and issues](https://juanp-g.github.io/MC-ServerLauncher/docs/articles/contributing.html#commits-pull-requests-and-issues)**.

🔒 **Found a security problem? Don't open an issue** — [`SECURITY.md`](SECURITY.md) has the private
channel, and lists the trade-offs that are already known and documented.

---

# Contribuir a MC Server Launcher

Gracias por querer echar una mano. La guía completa —compilar, las pruebas, el estilo de código, cómo
añadir un idioma, un tipo de servidor o sacar una versión— está en el sitio de documentación:

📖 **[Guía de contribución](https://juanp-g.github.io/MC-ServerLauncher/docs/articles/contributing.es.html)**
 · fuente: [`docs/articles/contributing.es.md`](docs/articles/contributing.es.md)
🏗️ **[Arquitectura](https://juanp-g.github.io/MC-ServerLauncher/docs/articles/architecture.es.html)**
 · fuente: [`docs/articles/architecture.es.md`](docs/articles/architecture.es.md)

## La versión corta

```powershell
git clone https://github.com/JuanP-G/MC-ServerLauncher.git
cd MC-ServerLauncher
dotnet run --project McServerLauncher                                  # ejecutarlo
dotnet test McServerLauncher.Tests/McServerLauncher.Tests.csproj       # probarlo
dotnet format McServerLauncher.sln                                     # dejarlo con el estilo de la casa
```

Cinco cosas que se miran siempre en una revisión:

1. **El estilo se aplica, no se discute.** Lo tiene el `.editorconfig` y lo aplica `dotnet format`.
   Los campos privados de instancia son `_camelCase`, los estáticos y las constantes `PascalCase` sin
   guion bajo, y los métodos asíncronos acaban en `Async` (salvo los `[RelayCommand]`, que se llaman
   como el botón).
2. **Se respeta la separación MVVM.** Lógica en `Services/` sin nada de interfaz, estado y comandos
   enlazables en `ViewModels/`, code-behind ligero en `Views/`, datos puros en `Models/`.
3. **Ningún texto visible va escrito a mano.** Cada cadena es una clave en los **cinco** `.resx` —el
   español en el `Strings.resx` neutral y las traducciones al lado—. Si falta una, falla una prueba.
4. **Las pruebas pasan en Windows *y* en Linux.** CI ejecuta las dos en todas las ramas, y varias
   usan sockets, pipes y bloqueos de archivo reales, que se comportan distinto en cada plataforma.
5. **La documentación se ha movido con el código**, y en **los dos idiomas**: el README para una
   función, `architecture.md` para un servicio o un flujo, `contributing.md` para una convención.

Los comentarios y los nombres del código van en inglés; solo se traduce lo que lee el usuario.

## Cómo se escribe

Los commits son **Conventional Commits en español** — `tipo(ámbito): asunto`, describiendo el
problema y no el mecanismo, con un cuerpo que cuenta qué pasaba antes y por qué se ha hecho así.
Activa la plantilla una vez y te va guiando:

```powershell
git config commit.template .gitmessage
```

El título de un pull request es el asunto de un commit; los títulos de los issues vienen ya con esa
forma. Las reglas completas, la lista de tipos y los ámbitos canónicos:
**[Commits, pull requests e issues](https://juanp-g.github.io/MC-ServerLauncher/docs/articles/contributing.es.html#commits-pull-requests-e-issues)**.

🔒 **¿Has encontrado un problema de seguridad? No abras un issue** — en [`SECURITY.md`](SECURITY.md)
está el canal privado, y la lista de compromisos que ya están asumidos y documentados.
