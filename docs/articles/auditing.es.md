# Auditorías del código

> 🇬🇧 Prefer English? Read the [English version](auditing.md).

Cada cierto tiempo, el código que ha entrado desde la última auditoría se vuelve a leer de arriba
abajo: una vez buscando fallos que una persona puede ver, otra buscando los que nadie ve hasta que
cuestan algo, y otra buscando código que se repite o que será difícil de ampliar. Esta página explica
cómo se hace y cómo se informa, para que dos auditorías separadas por un año se puedan comparar y para
que cualquiera —una persona que contribuye o un asistente de IA— haga la siguiente igual.

## Cuánto hace de la última

Cada auditoría deja una etiqueta anotada en el commit que revisó, `auditoria/AAAA-MM-DD`:

```powershell
git describe --tags --match 'auditoria/*'       # auditoria/2026-10-14-37-g1a2b3c4 → 37 commits desde entonces
git log --oneline auditoria/2026-10-14..main    # lo que tendrá que leer la siguiente
git diff --stat auditoria/2026-10-14..main -- McServerLauncher McServerLauncher.Tests
```

La etiqueta marca el código que se **leyó**, no el que se arregló: los arreglos vienen después, en sus
propios pull requests, y la siguiente auditoría también los lee. El [registro](#registro) de abajo dice
qué encontró cada una y dónde se arregló.

## Alcance

Desde la última etiqueta `auditoria/*` hasta `main`, en tres áreas, y cada archivo de un área se lee
entero; el diff solo dice qué archivos abrir:

| Área | Qué cubre |
|---|---|
| Configuración e interfaz | La página de configuración del servidor, la de servidor nuevo, las cajas de números, los bindings de las vistas |
| Contenido | Mods y plugins: instalar, actualizar, sustituir, limpiar, la tienda, Crossplay/Hydraulic/MultiVersion |
| Ciclo de vida | Arrancar, parar, copias y restauración, wake-on-demand, la prueba de conexión, la fontanería de la app |

Y en todas: las claves `.resx`, el texto que un error pone en pantalla y el estilo de la casa.

## Qué se busca

- **Texto interno en pantalla.** El mensaje de una excepción (`ex.Message`), el nombre de un tipo de
  .NET, inglés dentro de una frase en español, una clave de recurso en vez de su texto, un `{0}` sin
  rellenar, `NaN`. El usuario ya vio una vez `InvalidCastException: Could not convert '(null)' to
  System.Int32` debajo de una caja de números.
- **Bindings que fallan en silencio.** Las vistas usan `x:CompileBindings="False"`: un nombre de
  propiedad con otra mayúscula compila y simplemente no enseña nada. Cada ruta se compara con su
  contexto de datos.
- **Los cinco `.resx`.** Toda clave que se usa existe en todos, con los mismos huecos, y las claves que
  ya no usa nadie se quitan.
- **Estado que no vuelve.** Un indicador `Is*`, una puerta o una configuración a medio copiar que se
  quedan puestos cuando una excepción se va por otro camino.
- **Escrituras que pueden quedar a medias.** Un archivo del que la app depende se escribe con un
  nombre temporal y se mueve a su sitio; lo que se le enseña al usuario en una lista (un zip de copia)
  solo aparece cuando está completo.
- **Hilos.** Colecciones cambiadas fuera del hilo de la interfaz, `async void`, tareas lanzadas sin
  esperar cuyos fallos no ve nadie, cancelaciones que se aceptan y luego se ignoran.
- **Windows y Linux.** Rutas que distinguen mayúsculas, bloqueos de archivo que existen en un sistema
  y no en el otro, tests que se saltan fuera de Windows y no prueban nada en el otro runner de CI.
- **Dos respuestas a una pregunta.** Código nuevo que reescribe un helper que ya existe (se dice cuál y
  dónde), o la misma lógica tres veces con tres resultados un poco distintos.
- **El estilo de la casa.** `.editorconfig`, las reglas de nombres, 110 columnas, comentarios en inglés.

## Gravedad

| Gravedad | Qué significa |
|---|---|
| **Alta** | Se pierden o se dañan datos, un servidor no puede arrancar, o la interfaz dice algo del servidor que no es verdad |
| **Media** | Un comportamiento incorrecto que un usuario puede encontrarse usando la app normalmente, o texto interno en pantalla |
| **Baja** | Un caso límite, un mensaje que confunde, un coste pequeño que nadie notaría |
| **Diseño** | Duplicación o una forma que hace más difícil el siguiente cambio; hoy no se comporta mal |
| **Estilo** | Se sale del estilo de la casa |

## Comprobar antes de informar

Cada hallazgo se marca como **confirmado** —el camino del código se leyó de punta a punta, o el fallo
se reprodujo— o **plausible** —depende de un momento concreto, de un servicio de fuera o de un detalle
del framework que nadie ha comprobado aún—. Todo lo que se informa como alto se comprueba antes, y
comprobar puede bajarlo de grado: en la auditoría del 2026-10-14, «actualizar EssentialsX borra sus
módulos» se contrastó con la API de Modrinth, que publica un solo archivo por versión, y pasó de alta
a baja.

Un hallazgo que necesita la interfaz para estar seguro se reproduce antes de arreglarlo, en una app de
Avalonia de usar y tirar que aloja la vista de verdad: registra los errores de binding
(`LogToTrace(LogEventLevel.Warning)`) y hace una captura con `RenderTargetBitmap`. Compilar no prueba
nada de una vista cuyos bindings no se compilan.

## El informe

El informe se escribe para quien decide qué se arregla, así que cada hallazgo se cuenta como lo que un
usuario vería o perdería, no como lo que hace el código.

1. **Una página con los hallazgos** (una página HTML; las del mantenedor se guardan como artifacts de
   Claude):
   - arriba, el rango, la etiqueta y la etiqueta anterior;
   - un recuento por gravedad;
   - una lista que se filtra por gravedad y por área, con una fila por hallazgo: su gravedad, su área,
     **confirmado / plausible**, el pull request que lo arregla, un título que es el síntoma, el
     escenario concreto (entradas y estado → resultado equivocado) y el `archivo:línea` donde mirar.
2. **Una decisión de alcance**: qué se arregla ahora y qué va a un issue.
3. **Un issue de seguimiento** titulado `Auditoría AAAA-MM-DD`, con cada hallazgo como tarea.
4. **Una fila en el [registro](#registro).**

## Arreglar

- Pull requests **por área**, cada uno desde `main` en su propio worktree, cada uno con tests que
  fallan con el código viejo. Un cambio transversal en el que se apoyan los demás (un traductor de
  errores único, por ejemplo) va primero.
- Lo que enseña una auditoría entra en
  [Reglas de corrección aprendidas a golpes](contributing.es.md#reglas-de-corrección-aprendidas-a-golpes),
  en el mismo pull request que el arreglo del que sale.
- Cuando se mezcla el último arreglo, la fila del registro recibe sus pull requests y el issue se cierra.

## Registro

| Fecha | Etiqueta | Rango | Alta · Media · Baja · Diseño | Issue | Arreglado en |
|---|---|---|---|---|---|
| 2026-10-04 | `auditoria/2026-10-04` | toda la app, hasta `5c6d45e` | 40 arreglos (seguridad, fiabilidad, rendimiento) | — | #34, #35 |
| 2026-10-14 | `auditoria/2026-10-14` | `auditoria/2026-10-04..989678f` (v1.17.0 → 1.19.1) | 6 · 13 · 17 · 8 | #55 | #49–#54 (en curso) |
