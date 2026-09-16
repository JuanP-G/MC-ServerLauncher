# Security Policy · Política de seguridad

**🇬🇧 English · 🇪🇸 [Español](#español)**

## Reporting a vulnerability

**Please do not open a public issue.** Use GitHub's private reporting instead:

👉 **[Report a vulnerability](https://github.com/JuanP-G/MC-ServerLauncher/security/advisories/new)**

This matters more here than in most desktop apps, because MC Server Launcher **downloads and
executes code on the user's machine**: Java runtimes from Adoptium, server jars from Mojang, Paper
and Purpur, mod-loader *installers* that are run with `java -jar`, mods and plugins from Modrinth,
and Playit's own `playitd` binary. Anything that lets one of those be substituted, or that gets a
checksum check skipped, is worth reporting privately first.

Please include what you would need yourself: the version, the platform, what an attacker would have
to control, and what they would get. A proof of concept is welcome; working exploit code is not
required.

### What is already assumed

Some things are documented trade-offs rather than findings, and each is argued in the service that
owns it (see [Architecture](https://juanp-g.github.io/MC-ServerLauncher/docs/articles/architecture.html)):

- **Fabric** publishes no checksum for its server jar, so it is validated structurally instead.
- **Purpur** publishes only an MD5; HTTPS from its own API is what authenticates the build, and the
  hash is there to catch a truncated download.
- **Forge** publishes a `.sha1` from the same server as the artifact, so the check protects against
  corruption, not against that server being compromised. Same for NeoForge with a `.sha256`.

A report that one of these is not a cryptographic guarantee is already known. A report that one of
them is *not being enforced at all* is a real finding.

### Supported versions

Only the **latest release** is supported. Fixes ship in a new version rather than as patches to old
ones, and the app updates itself on all three platforms.

---

<a name="español"></a>

## Informar de una vulnerabilidad

**Por favor, no abras un issue público.** Usa el canal privado de GitHub:

👉 **[Informar de una vulnerabilidad](https://github.com/JuanP-G/MC-ServerLauncher/security/advisories/new)**

Aquí importa más que en la mayoría de aplicaciones de escritorio, porque MC Server Launcher
**descarga y ejecuta código en la máquina del usuario**: runtimes de Java de Adoptium, jars de
servidor de Mojang, Paper y Purpur, *instaladores* de cargadores de mods que se ejecutan con
`java -jar`, mods y plugins de Modrinth, y el propio binario `playitd` de Playit. Cualquier cosa que
permita sustituir uno de ellos, o que consiga saltarse una comprobación de checksum, merece un
informe privado antes que nada.

Incluye lo que necesitarías tú: la versión, la plataforma, qué tendría que controlar un atacante y
qué conseguiría. Una prueba de concepto es bienvenida; un exploit funcionando no hace falta.

### Lo que ya está asumido

Algunas cosas son compromisos documentados y no hallazgos, y cada uno está argumentado en el
servicio que lo aplica (ver [Arquitectura](https://juanp-g.github.io/MC-ServerLauncher/docs/articles/architecture.es.html)):

- **Fabric** no publica checksum de su jar de servidor, así que se valida su estructura.
- **Purpur** solo publica un MD5; lo que autentica la build es el HTTPS de su propia API, y el hash
  está para detectar una descarga truncada.
- **Forge** publica un `.sha1` desde el mismo servidor que el artefacto, así que la comprobación
  protege de una corrupción, no de que ese servidor esté comprometido. Lo mismo con NeoForge y su
  `.sha256`.

Un informe que diga que esto no es una garantía criptográfica ya se sabe. Un informe que diga que
alguno de ellos **no se está comprobando** sí es un hallazgo.

### Versiones con soporte

Solo la **última versión publicada**. Los arreglos salen en una versión nueva, no como parches sobre
las antiguas, y la app se actualiza sola en las tres plataformas.
