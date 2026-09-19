# Cómo contribuir

> 🇬🇧 Prefer English? Read the [English version](contributing.md).

¡Gracias por querer ayudar! Esta guía explica cómo compilar el proyecto y cómo hacer los cambios más
habituales.

## Requisitos

- **Windows**, **Linux** o **macOS** (la interfaz usa Avalonia, multiplataforma).
- **.NET 9 SDK**.
- *(Solo para generar el instalador)* **Inno Setup 6**.

## Compilar y ejecutar

```powershell
git clone https://github.com/JuanP-G/MC-ServerLauncher.git
cd MC-ServerLauncher
dotnet run --project McServerLauncher
```

Ejecutar las pruebas:

```powershell
dotnet test McServerLauncher.Tests/McServerLauncher.Tests.csproj
```

Generar el sitio de documentación en local (esta página):

```powershell
dotnet tool install -g docfx   # solo la primera vez
.\docs\build-docs.ps1          # compila y sirve en http://localhost:8080
```

## Pruebas

`McServerLauncher.Tests/` es un proyecto de **xUnit** que referencia la app directamente — la lógica
de decisión que merece la pena probar es `internal` a propósito (es implementación, no API) y llega a
las pruebas por `InternalsVisibleTo`, así que renombrar un miembro rompe la compilación en vez de
fallar en ejecución como haría la reflexión.

`.github/workflows/tests.yml` las ejecuta **en todas las ramas** y en **Windows y Linux a la vez**.
Es deliberado: varias usan sockets, named pipes y bloqueos de archivo reales, y .NET los implementa
de forma distinta en cada plataforma (una named pipe es un socket de dominio Unix en Linux). Una
prueba que solo tenga sentido en una plataforma debe saltarse a sí misma en la otra, no comprobar
otra cosa.

Las pruebas que necesitan controles reales de Avalonia usan `AvaloniaFixture`, una única aplicación
headless compartida por toda la ejecución — Avalonia solo se puede inicializar una vez por proceso, y
sus controles hay que tocarlos desde el hilo que la inicializó. Existe porque un fallo se publicó dos
veces y nada más lo pillaba: el selector de tipo informando de la selección *anterior* dentro de su
propio evento de cambio.

Van en seis capas, y conviene saber a cuál pertenece una prueba nueva:

| Capa | Ejemplo | Qué ve |
|---|---|---|
| Los modelos solos | `ServerConfigFormatTests`, `ModelNotificationTests` | Qué se escribe en disco; qué propiedades avisan |
| La tabla | `ServerConfigEffectsTests` | Que no se ha quedado ningún campo de la config sin declarar |
| View models, construidos de verdad | `ServerViewModelRefreshTests`, `MainViewModelFlowTests` | Que un cambio en la config llega a la tarjeta, a los paneles y a `servers.json` |
| Controles reales | `AddEditServerDialogTests`, `CreateServerDialogTests` | Que llega a la pantalla — la mitad que una prueba unitaria no ve |
| Puertas sobre el código | `StartDependencyGateTests`, `StoreHashTests`, `ExportSelectionTests` | Que una promesa se cumple en el propio archivo — sin red, una sola definición |
| El artefacto, ejecutado | `InstallScriptSmokeTests` | Que el script que recibe un jugador funciona de verdad — con bash, en Linux en la CI |

`ServerViewModel` y `MainViewModel` aceptan una carpeta de datos y no arrancan nada hasta
`Activate()`, así que una prueba puede tener uno sin tocar `%APPDATA%`, Playit ni la red. Las pruebas
nunca llaman a `Activate()`. `ServerModsView` no se puede renderizar en headless (la fuente de
iconos), así que sus enlaces se comprueban leyendo el `.axaml` y mirando el view model por reflexión
— ver `MissingDependencyPanelTests`.

## Estilo de código

Todo el repositorio está escrito con un mismo estilo, y la mayor parte **se aplica sola**: el
`.editorconfig` de la raíz tiene las reglas de formato y de nombres, y Visual Studio, Rider y VS Code
lo leen sin configurar nada. Antes de abrir un pull request:

```powershell
dotnet format McServerLauncher.sln
```

Así no queda nada que discutir en la revisión. El `.gitattributes` zanja igual los finales de línea
—el repositorio guarda LF y a tu copia de trabajo le da lo que espere tu plataforma—, para que quien
trabaje en Windows y quien trabaje en Linux no produzcan nunca un diff donde han cambiado todas las
líneas y no ha cambiado nada.

La plantilla de pull request repite la lista de comprobación, así que nada de esto hay que recordarlo
justo en el momento en que hace falta. Las reglas de abajo son las que conviene saberse, y las pocas
que una herramienta no puede comprobar.

### Nombres

| Qué | Estilo | Ejemplo |
|---|---|---|
| Tipos, métodos, propiedades, eventos | `PascalCase` | `ServerProcessManager`, `EnsureJavaAsync` |
| Campos privados de instancia | `_camelCase` | `private readonly ServerConfig _config;` |
| Campos privados **estáticos** y `const` | `PascalCase`, sin guion bajo | `private static readonly HttpClient Http` |
| Locales y parámetros | `camelCase` | `var levelName = …` |
| Interfaces / parámetros de tipo | `IPascalCase` / `TPascalCase` | `IProgress<string>`, `TResult` |

El guion bajo de las dos primeras filas es justo el motivo de la regla: es como distingues el estado
de la instancia de todo lo demás sin tener que buscar, y por eso un campo `static` **no** lo lleva.

**Los métodos asíncronos acaban en `Async`.** La única excepción es un método `[RelayCommand]`, que
se llama como el botón (`private async Task Start()` → `StartCommand`); el toolkit genera el comando
a partir de ese nombre, y `StartAsyncCommand` se lee peor en todos los sitios donde se enlaza.

Los nombres dicen *para qué sirve la cosa*, no de qué está hecha: `CrossplayService`, no
`GeyserHelper`. Un nombre que necesita un comentario para entenderse es culpa del comentario la mitad
de las veces y del nombre la otra mitad — casi siempre sale mejor arreglar el nombre.

### Declarar estado

- Una clase empieza por sus colaboradores, `private readonly` e inicializados en la misma línea:
  `private readonly ModrinthService _modrinth = new();`. Ese bloque es donde quien lee se entera de
  qué está hecha la clase, y por eso **aquí no se usan constructores primarios**: esconden eso detrás
  de una lista de parámetros.
- El estado enlazable es `[ObservableProperty] private string _searchQuery = string.Empty;` y nada
  más. Nunca un `OnPropertyChanged` escrito a mano.
- Lo derivado es una propiedad con cuerpo de expresión:
  `public bool UpdateAvailable => Update is not null;`
- **Un modelo que se guarda y que un diálogo edita mientras está en pantalla es observable.**
  `ServerConfig` y `NotificationSettings` derivan de `ObservableObject`, y una propiedad nueva en
  cualquiera de los dos entra como `[ObservableProperty] private T _foo;` igual que en todas partes.
  No cambia lo que se escribe en disco, y `ServerConfigFormatTests` está para demostrar que sigue
  siendo así. `AppSettings` es la excepción y sigue plano: su diálogo edita una copia y la vuelca al
  aceptar.
- **Un campo nuevo en `ServerConfig` necesita una fila en `ServerConfigEffects`**: qué propiedades
  de view model alimenta y qué hay que rehacer, o una línea diciendo por qué no lo enseña nadie.
  `ServerConfigEffectsTests` falla hasta que está, y el fallo dice qué escribir. No es burocracia:
  un campo que se queda fuera enseña la respuesta correcta hasta que alguien edita ese servidor, y
  la equivocada desde entonces hasta que se reinicia la app.
- `var` cuando el tipo ya está en la línea (`var dialog = new SettingsDialog(…)`), y el tipo escrito
  cuando no lo está.
- **Un constructor monta; `Activate()` arranca.** Nada que sondee, abra un socket, se suscriba a un
  singleton compartido o vaya a la red va en un constructor: va en `Activate()`, cuyo espejo es
  `ShutdownAsync()`, y los dos tienen que aguantar que se les llame dos veces. `ServerViewModel` y
  `MainViewModel` están partidos así, que es la única razón de que se puedan construir en una prueba.

### Capas

- Mantén la separación **MVVM**: lógica en `Services/` (sin interfaz), estado y comandos enlazables
  en `ViewModels/`, solo code-behind ligero en `Views/`, y datos puros en `Models/`.
- Una decisión de color se parte en dos: los hex en una clase sin interfaz de `Services/`, los brushes
  de Avalonia al lado en `ViewModels/` — `ConsoleColors`/`ConsolePalette`,
  `NotificationPalette`/`NotificationBrushes`, `ServerTypeCatalog`/`ServerTypeBrushes`. Un color que
  acaba en `settings.json` no puede necesitar un tipo que sepa que Avalonia existe.
- Los estilos que necesita más de una vista van en `Styles/Shared.axaml`, no dentro del
  `<Window.Styles>` de una vista. **Ponle a un estilo compartido un nombre que signifique una sola
  cosa:** dos vistas tuvieron cada una su `Border.card` queriendo decir cosas distintas, y Avalonia
  las mantuvo separadas solo porque eran locales — al unirlas habrían chocado y una vista habría
  cambiado de aspecto sin que fallara nada.
- **Nada de rutas absolutas del equipo.** Usa `Environment.GetFolderPath(...)`; los datos por usuario
  viven en `%APPDATA%\McServerLauncher\`.

### Comentarios y documentación

- **Los comentarios y los nombres del código van en inglés**, en todo lo que lee quien contribuye:
  `McServerLauncher/` (los `.axaml` incluidos), `McServerLauncher.Tests/` y `.github/workflows/`.
  El texto que ve el usuario **no** se escribe a mano: pasa por el sistema de localización (ver
  abajo). Los scripts propios del mantenedor para publicar y para la web (`publish.ps1`,
  `installer/`, `tools/`, `web/_i18n/`) siguen en español, tanto los comentarios como lo que
  imprimen; son la única excepción documentada, no un sitio del que copiar la costumbre.
- Los tipos públicos llevan un resumen XML `///`, y también cualquier miembro cuyo contrato no se
  entienda por el nombre. Es lo que alimenta la referencia de API.
- Un comentario dice **por qué**, no qué. Los que merecen la pena aquí son los que dejan constancia
  de una decisión y de qué pasaba sin ella — "Purpur solo publica un MD5, y este es el motivo de que
  baste" vale un párrafo; "// recorrer los mods" vale un borrado.
- Ajusta los bloques `///` y la prosa a unas **100 columnas**, y el código a unas **110**.

### Reglas de corrección aprendidas a golpes

Cada una está aquí porque saltársela publicó un fallo que ninguna prueba pilló en su momento.

- **Un fallo nunca puede parecer una buena noticia.** Cuando una consulta no se puede hacer, se
  devuelve `null` o un estado distinto — nunca el resultado vacío que también significa «no hay nada
  que hacer». La comprobación de actualizaciones decía «todo está actualizado» sin conexión, y
  Modrinth contestaba a los hashes en mayúsculas con un `200 OK` vacío, así que «Buscar
  actualizaciones» no encontró nada mientras existió. Nadie reporta buenas noticias.
  `UpdateCheckTests` sujeta el tipo de retorno por reflexión justo por eso.
- **Una pregunta, una definición.** Cuando dos sitios responden a lo mismo, se pasan los dos por uno
  y se añade una prueba que falle si aparece una segunda copia. Se ha separado tres veces: el
  emparejamiento de túneles (`PlayitApiService.Match`), los hashes que van a la tienda
  (`ModrinthService.ApiHashes`) y las dependencias que declara un jar (`ContentManifest`). Cada vez
  solo una de las copias aprendió lo que hacía falta.
- **La prueba de un arreglo tiene que fallar con el código de antes.** Compruébalo: aparta el
  arreglo, ejecuta la prueba y mírala ponerse roja. Una prueba que pasa en los dos casos no demuestra
  nada, y es fácil escribirla.
- **Lee lo que lee el cargador.** Los mods llevan otros mods dentro (`META-INF/jars/`,
  `META-INF/jarjar/`), y los cargadores traen librerías propias (`mixinextras`). Todo lo que decida
  qué hay instalado tiene que ver las dos cosas, o dará por perdido lo que el servidor carga sin
  problema.
- **Los recursos incrustados con puntos en el nombre necesitan `WithCulture=false` y un
  `LogicalName`.** MSBuild deduce una cultura de los segmentos del nombre, `sh` es una de verdad, e
  `install-mods-unix.sh.in` acabó en silencio en un ensamblado satélite con la compilación en verde.
- **Lo que tiene que sobrevivir a una reconstrucción no puede vivir en lo que se reconstruye.** La
  pestaña de Mods reconstruye sus filas desde el disco después de cada actualización, activación y
  borrado, y las actualizaciones que encontraba una comprobación vivían en las filas — así que
  actualizar un mod hacía desaparecer los botones de todos los demás. Ese estado va en el view model y
  se le devuelve a cada fila nueva.
- **Una opción de la JVM que añade la app va condicionada a la versión de Java.** Una opción que la
  JVM no reconoce le impide arrancar, que es peor que cualquier aviso que se quisiera callar. Ver
  `ServerProcessManager.ImpliedJvmFlags`, y nunca por encima de lo que el usuario puso en sus
  argumentos extra.
- **Scripts escritos para jugadores.** La lógica en una plantilla `.in`, cada línea visible en los
  .resx, y cada marcador sustituido por un mensaje entero y terminado — nunca una cadena de formato.
  Finales de línea por archivo (`.sh` LF, `.bat` CRLF), sin BOM en ningún sitio, y todo `choice` de
  un `.bat` lleva `/t` y `/d`, porque batch no sabe si hay alguien para pulsar una tecla.

## Commits, pull requests e issues

Todo lo que se escribe sobre un cambio sigue una misma forma, para que `git log` se lea como un
historial de decisiones y no como una lista de ediciones. Es **Conventional Commits, en español**:

```
tipo(ámbito): qué cambia, contado como se lo contarías a alguien

Qué pasaba antes, y por qué era un problema de verdad. Qué hace ahora. Por qué
así y no de la otra forma, con lo que se descartó.

Closes #12
```

Ejecuta esto una vez y git te abrirá esa forma, con las reglas como comentarios:

```powershell
git config commit.template .gitmessage
```

### El asunto

En español, en minúscula, sin punto final, unos **70 caracteres** (90 como tope). Describe el
**problema o el resultado, no el mecanismo** — qué líneas se han movido ya lo dice el diff:

| | |
|---|---|
| ✅ | `fix(bedrock): el puerto que no aparecía, y los túneles que se pisaban` |
| ❌ | `fix(bedrock): cambiar PickBedrockPort para usar accountTunnels` |

Dos cosas relacionadas se unen con `, y`. Si no caben en una frase, son dos commits.

### Tipos

| Tipo | Para |
|---|---|
| `feat` | Algo nuevo que puede hacer el usuario |
| `fix` | Un comportamiento que estaba mal |
| `seg` | Seguridad: verificar, sanear, acotar, cifrar |
| `perf` | Más rápido o más barato, sin cambiar lo que hace |
| `refactor` | Misma conducta, mejor forma |
| `docs` | Documentación — los `.md`, los comentarios `///`, la web |
| `test` | Pruebas, y solo pruebas |
| `ci` | Workflows de GitHub Actions y empaquetado |
| `chore` | Dependencias, tooling, limpieza |
| `release` | Siempre `release: X.Y.Z`, sin ámbito |

`seg` es un tipo propio a propósito, y no un sabor de `fix`. Esta app descarga y ejecuta código en la
máquina del usuario, así que «¿qué hemos cambiado de eso desde la última auditoría?» es una pregunta
que alguien hace cada cierto tiempo — y tiene que poder responderse con
`git log --grep '^seg'` en vez de leyendo cuatrocientos commits.

### Ámbitos

El área funcional, en español, opcional. Usa uno que ya exista antes de inventar otro, y usa **una
sola forma por concepto** — en el historial hay `notif` y `notificaciones`, que es exactamente para
lo que está esta lista:

`consola` · `mods` · `tienda` · `jugadores` · `servidor` · `tipos` · `crossplay` · `bedrock` ·
`playit` · `java` · `paper` · `forge` · `fabric` · `puertos` · `backups` · `avisos` · `colores` ·
`actualizaciones` · `ajustes` · `nombres` · `seguridad` · `datos` · `ui` · `web` · `tests` · `ci` ·
`release`

¿Añades uno que se va a repetir? Añádelo a esta lista en el mismo commit.

### El cuerpo

En español, a **80 columnas**, y se espera en todo lo que no sea trivial. Escribe lo que un
`git blame` dentro de dos años necesita y no puede sacar del diff: cuál era el comportamiento
anterior, por qué era un problema de verdad, y qué descartaste por el camino. El historial que ya
hay es la referencia — `git show bf6453a` es un buen ejemplo que leer antes de escribir el primero.

Los pies son `Closes #12`, `Refs #12`, o `BREAKING CHANGE:` con qué se rompe y qué hacer al respecto.

### Pull requests

- **El título es el asunto de un commit** — mismos tipos, mismos ámbitos, mismo español.
  `.github/pull_request_template.md` rellena el resto, la lista de comprobación incluida.
- La descripción es el cuerpo del commit que habrías escrito para toda la rama.
- Una idea por pull request. El commit de mezcla es
  `Merge pull request #N: <título corto> (versión si aplica)`.

### Issues

Abrir uno pasa por un formulario (`.github/ISSUE_TEMPLATE/`), que deja el título con la misma forma:
un fallo empieza por `fix(ámbito): ` y una propuesta por `feat(ámbito): `. **Escribe en inglés o en
español, el que prefieras** — los dos se leen.

Un **problema de seguridad nunca es un issue público.** [`SECURITY.md`](https://github.com/JuanP-G/MC-ServerLauncher/blob/main/SECURITY.md)
explica el canal privado y enumera los compromisos que ya están asumidos, para que puedas distinguir
un hallazgo de una decisión documentada.

## La documentación es parte del cambio

En este repositorio la documentación es parte del cambio, no trabajo para después:

- Una **función** nueva o distinta → `README.md` y `README.es.md`, los dos.
- Un **servicio, flujo o carpeta** nuevos → `docs/articles/architecture.md` y `architecture.es.md`.
- Una **convención o flujo de trabajo** nuevos → este archivo y `contributing.es.md`, y
  `.github/copilot-instructions.md` para que los asistentes de IA sugieran lo mismo que pediría una
  revisión.
- Un **texto nuevo visible para el usuario** → los cinco `.resx` (esto lo comprueba una prueba).

Las páginas en inglés y en español son el mismo documento dos veces. Actualizar una y no la otra es
justo como alguien acaba fiándose de una página que va una versión por detrás sin saberlo.

## Recetas paso a paso

### Añadir un idioma
1. Copia `Resources/Strings.resx` a `Resources/Strings.<código>.resx` (p. ej. `Strings.it.resx`) y
   traduce cada `<value>`.
2. Añade el código a `<SatelliteResourceLanguages>` en `McServerLauncher.csproj`.
3. Añádelo a la lista `Languages` de `MainViewModel` para que salga en el selector de la barra lateral.

### Añadir un texto traducible
1. Añade la misma entrada `<data name="MiClave">` a **los cinco** archivos `.resx` con la traducción.
2. Úsalo desde XAML como `{loc:Loc MiClave}`, o desde código como `Localizer.Get("MiClave")`
   (usa `string.Format(Localizer.Get("MiClave"), arg)` si tiene huecos `{0}`).

### Añadir un ajuste de `server.properties` al editor visual
1. Añade el control + etiqueta/descripción en `Views/ServerConfigDialog.axaml` (y enlázalo en su
   code-behind).
2. Lee/escribe la clave con `ServerPropertiesService.Read` / `Update`, que conserva el resto del
   archivo, los comentarios y el orden.

### Añadir un tipo de servidor
1. Una fila en `Services/ServerTypeCatalog.cs`: nombre visible, familia (plugins / mods / ninguna),
   color de la insignia y su `CrossplayLevel`. El selector, las insignias, la tienda, la carpeta de
   contenido y las reglas de crossplay leen todos de esa tabla.
2. Una rama en `Services/ServerJarInstaller.cs`, que es el único sitio que sabe cómo se obtiene cada
   tipo. El diálogo de creación y el de cambio de tipo llaman los dos ahí.
3. Añade la clave de descripción del tipo a los cinco `.resx` (la línea bajo su nombre en el
   selector).
4. **Nunca renumeres el enum `ServerType`.** `servers.json` lo guarda como entero, así que mover un
   miembro reinterpreta todos los servidores ya guardados en todas las máquinas. Los tipos nuevos van
   al final.

> Resiste la tentación de añadir un `switch` sobre `ServerType` en ningún otro sitio. Seis de ellos
> es lo que sustituyó el catálogo, y un tipo presente en cinco de los seis parecía correcto y se
> comportaba como otra cosa.

### Añadir un diálogo o servicio nuevo
- **Diálogo:** crea `Views/MiDialogo.axaml` + `.axaml.cs` como una `Window` normal de Avalonia,
  localiza sus textos con `{loc:Loc ...}` y ábrelo desde un comando del ViewModel (mira
  `WhatsNewDialog` o `PlayitApiKeyDialog` como ejemplos pequeños).
- **Servicio:** añade una clase centrada en `Services/`, sin interfaz, e instánciala desde el
  ViewModel correspondiente (mira cómo `ServerViewModel` compone sus servicios).

### Sacar una versión nueva
1. Sube `<Version>` en `McServerLauncher/McServerLauncher.csproj` — es la **fuente única de verdad**.
   `publish.ps1` la lee y se la pasa a Inno Setup. Mantén alineado el fallback `MyAppVersion` del
   `.iss` solo para que una compilación manual/directa con Inno Setup no genere un nombre antiguo.
2. Añade una entrada de **novedades** para que el diálogo de actualización tenga algo que mostrar:
   - Una nueva tupla al principio de `Entries` en `Services/Changelog.cs` (la más nueva primero),
     p. ej. `(new Version(1, 6, 0), "Whatsnew_1_6_0")`.
   - La clave `Whatsnew_x_y_z` correspondiente en **los cinco** archivos `.resx` (el texto en español
     en el neutral `Strings.resx`, las traducciones en el resto). Mira *Añadir un texto traducible*
     más arriba.
3. Ejecuta `publish.ps1`. Publica el build self-contained `win-x64`, genera el instalador de Inno
   Setup en `dist/` **y** escribe `dist/SHA256SUMS.txt` junto a él.
4. Crea la release con **ambos** assets y notas **bilingües**:
   ```powershell
   gh release create vX.Y.Z dist/MC-ServerLauncher-Setup-X.Y.Z.exe dist/SHA256SUMS.txt
   ```
   - El actualizador de la app busca el asset `.exe` (así que adjúntalo siempre) **y** el
     `SHA256SUMS.txt`, que usa para verificar el instalador antes de ejecutarlo
     (`UpdateService.CheckAsync`). La verificación es **obligatoria**: sin ese archivo el
     actualizador rechaza la instalación silenciosa y solo abre la página de la release — no lo
     omitas nunca.
> **Antes de cambiar cómo se numeran las versiones, comprueba qué sabe leer la versión instalada.**
> Una release cuyo tag tiene una forma que los clientes antiguos no entienden es invisible para
> ellos por muy correcto que sea el código nuevo — y el código que la entiende viaja *dentro* de esa
> release. Ha pasado dos veces: las pre-releases eran invisibles antes de la 1.10.1, y los tags de
> cuatro números antes de la 1.10.3.1, porque en ambos casos había que cambiar `UpdateService` para
> verlas. La primera release de una forma nueva siempre hay que pasarla a mano; dílo en vez de
> prometer una actualización automática.

> **Numeración.** Una beta lleva un cuarto número que prolonga la estable a la que sigue: tras
> `1.10.3` vienen `1.10.3.1`, `1.10.3.2`, y lo terminado sale como la siguiente estable (`1.11.0`).
> Es así a propósito: numerar las betas según la versión a la que llevan haría que la estable
> quedara por debajo de sus propias betas y dejara tirado a quien las probó.

5. **Si es una beta**, publícala como pre-release desde su rama:
   ```powershell
   gh release create vX.Y.Z --prerelease --target <rama> dist/MC-ServerLauncher-Setup-X.Y.Z.exe dist/SHA256SUMS.txt
   ```
   GitHub deja las pre-releases fuera de `/releases/latest`, así que a quien está en la línea estable no se le
   empuja a una. Desde la 1.10.1 el actualizador lee la *lista* de releases, que es lo que hace que una beta sea
   alcanzable, y avisa de que lo es antes de que se pulse Actualizar. Cualquier versión anterior a la 1.10.1 no
   puede ver betas, así que la primera beta después de una estable hay que pasarla a mano.
6. Publicar la release dispara automáticamente los workflows de **Linux** (`release-linux.yml`) y
   **macOS** (`release-macos.yml`), que generan y adjuntan el `.AppImage` y los dos `.dmg`. No los
   subas a mano — basta con esperar a que terminen los workflows.

## Sitio de documentación (GitHub Pages)

La referencia de API + estos artículos se publican automáticamente en GitHub Pages mediante
`.github/workflows/docs.yml` en cada push a `main`. El sitio se genera con **DocFX** a partir de los
comentarios `///` y el markdown de `docs/`.

> **Paso único del propietario del repo:** activar Pages en **Settings → Pages → Source: “GitHub
> Actions”**. A partir de ahí, cada push actualiza el sitio solo.
