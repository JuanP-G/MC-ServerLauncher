# Security Policy · Política de seguridad

**English · [Español](#español)**

## Reporting a vulnerability

**Please do not open a public issue.** Use GitHub's private reporting instead:

**[Report a vulnerability](https://github.com/JuanP-G/MC-ServerLauncher/security/advisories/new)**

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
- **The app's own updates** are checked twice. `SHA256SUMS.txt` (`<package>.sha256` on Linux and
  macOS) comes from the same GitHub release as the package, so it only catches a corrupted download.
  What protects against a compromised account or release is the **signature** (`<package>.sig`),
  made with a private key that does not live on GitHub and checked against the public keys built into
  the app; without a valid one nothing is installed. The limits are in
  [Signed updates](https://juanp-g.github.io/MC-ServerLauncher/docs/articles/signing.html): versions
  before the first signed one (1.21.1) check nothing, an old release that was correctly signed is still
  correctly signed, and a signature proves who published a package, not that the build was clean.
  Windows `.exe` code signing (Authenticode) is not done.
- **The memory agent** is code that runs *inside* the JVM of every Automatic server. It is ours (one
  class, about 10 KB, no dependencies), embedded in the app instead of downloaded, and rebuilt byte for
  byte from `agent/` by a CI check; its SHA-256 is verified each time it is copied into a server's
  folder, and a JVM that cannot load it makes the app start the server without it. It opens no
  connection and reads only a 1 KB `control` file in the server's own hidden folder, applying three
  heap options after validating them. Whoever can write there can already run anything as the server,
  so the file is not a new boundary. It can be turned off in Settings or with `MCSL_NO_AGENT=1`.

A report that one of these is not a cryptographic guarantee is already known. A report that one of
them is *not being enforced at all* is a real finding.

### Supported versions

Only the **latest release** is supported. Fixes ship in a new version rather than as patches to old
ones, and the app updates itself on all three platforms.

---

<a name="español"></a>

## Informar de una vulnerabilidad

**Por favor, no abras un issue público.** Usa el canal privado de GitHub:

**[Informar de una vulnerabilidad](https://github.com/JuanP-G/MC-ServerLauncher/security/advisories/new)**

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
- **Las actualizaciones de la propia app** se comprueban dos veces. `SHA256SUMS.txt`
  (`<paquete>.sha256` en Linux y macOS) viene de la misma release de GitHub que el paquete, así que
  solo detecta una descarga dañada. Lo que protege de una cuenta o una release comprometidas es la
  **firma** (`<paquete>.sig`), hecha con una clave privada que no vive en GitHub y comprobada contra
  las claves públicas incluidas en la app; sin una válida no se instala nada. Los límites están en
  [Actualizaciones firmadas](https://juanp-g.github.io/MC-ServerLauncher/docs/articles/signing.es.html):
  las versiones anteriores a la primera firmada (la 1.21.1) no comprueban nada, una release antigua bien firmada
  sigue estando bien firmada, y una firma prueba quién publicó un paquete, no que la build estuviera
  limpia. La firma de código del `.exe` de Windows (Authenticode) no está hecha.
- **El agente de memoria** es código que se ejecuta *dentro* de la JVM de cada servidor Automático. Es
  nuestro (una clase, unos 10 KB, sin dependencias), va incrustado en la app en vez de descargarse, y una
  comprobación de la CI lo recompila byte a byte desde `agent/`; su SHA-256 se verifica cada vez que se
  copia a la carpeta de un servidor, y si una JVM no puede cargarlo la app arranca el servidor sin él. No
  abre ninguna conexión y solo lee un archivo `control` de 1 KB en la carpeta oculta del propio servidor,
  y aplica tres opciones de heap tras validarlas. Quien pueda escribir ahí ya puede ejecutar lo que quiera
  como el servidor, así que el archivo no es una frontera nueva. Se apaga en Ajustes o con
  `MCSL_NO_AGENT=1`.

Un informe que diga que esto no es una garantía criptográfica ya se sabe. Un informe que diga que
alguno de ellos **no se está comprobando** sí es un hallazgo.

### Versiones con soporte

Solo la **última versión publicada**. Los arreglos salen en una versión nueva, no como parches sobre
las antiguas, y la app se actualiza sola en las tres plataformas.
