# Visibilidad y estadísticas

> Read this in [English](visibility.md).

Cómo se mide quién llega a la web y quién descarga la app, y dónde conviene estar para que más gente
la encuentre. La app no lleva telemetría y no se va a añadir sin que sea opcional y explícita: aquí solo se
mide la web y lo que GitHub ya publica.

## Qué se mide y con qué

| Qué | Herramienta | Qué guarda |
| --- | --- | --- |
| Visitas a la web, país, idioma, dispositivo, de dónde llegan | [GoatCounter](https://www.goatcounter.com) | Sin cookies ni banner. No guarda la IP. Gratis para proyectos sin ánimo de lucro. |
| Clics en Descargar (por sistema), GitHub, Issues, cambio de idioma | Eventos de GoatCounter (`data-goatcounter-click` en `web/index.html`) | Un contador por botón, sin datos personales. |
| Qué guía lleva a una descarga | Eventos `guia-<id>-cta`, `guia-<id>-descarga-win/linux/mac`, `guia-<id>-mas-<otra>` y `guia-<id>-landing` | Igual: un contador por botón y por guía. |
| Descargas reales de cada instalador | Contadores de GitHub Releases | Total acumulado por archivo. |
| Visitas, clones y referrers del repositorio | API de tráfico de GitHub | Solo los últimos 14 días. |

Un clic en «Descargar» no es una descarga: la persona puede cancelar en GitHub. Las **descargas reales**
salen de los contadores de las releases; los clics sirven para comparar botones y sistemas.

## Puesta en marcha (una sola vez)

1. **GoatCounter.** Crea la cuenta en goatcounter.com con el código `mc-serverlauncher` (la web ya apunta
   a `https://mc-serverlauncher.goatcounter.com`). Si ese código estuviera ocupado, elige otro y cámbialo
   en `web/index.html` (una línea con `data-goatcounter`), y ejecuta `python web/_i18n/build.py`.
   Opcional: en *Settings → Site* activa «Allow adding visitors to this site...» para tener un panel
   público; es un buen gesto de transparencia.
2. **Foto diaria de GitHub.** El workflow `stats.yml` guarda cada día descargas y estrellas en la rama
   `stats`. Para guardar también el tráfico hace falta un secreto: crea un token personal con permiso de
   escritura en este repositorio y guárdalo en *Settings → Secrets and variables → Actions* con el
   nombre `TRAFFIC_TOKEN`. La API de tráfico no acepta el token que GitHub da a los workflows.
   Para que las visitas de la web entren en la misma foto, crea un token de la API de GoatCounter
   (*Settings → API*, solo «Read statistics» y solo este sitio) y guárdalo como secreto `GOATCOUNTER_TOKEN`.
3. **Lanzarlo ya**, sin esperar a mañana: pestaña *Actions → Daily statistics snapshot → Run workflow*.

## Leer los datos

```bash
git fetch origin stats
git worktree add ../stats-data origin/stats
python tools/stats_report.py ../stats-data
```

Muestra descargas por sistema y versión, cuánto han crecido en 7 y 30 días (aparece `n/d` hasta que
haya fotos de esa antigüedad), visitas al repositorio y de dónde vienen. Los CSV (`downloads.csv`,
`repo.csv`, `views.csv`, `clones.csv`, `referrers.csv`, `paths.csv`) se abren tal cual en una hoja de cálculo.

## Dónde estar para que te encuentren

- **Google Search Console**: ya verificada. Revisa *Rendimiento* para ver con qué búsquedas aparece.
- **Bing Webmaster Tools** (lo que usa Edge): entra con la misma cuenta de Google e **importa** la
  propiedad desde Search Console. `web/_i18n/ping-indexnow.py` ya avisa de los cambios a Bing por IndexNow.
- **AlternativeTo**: la ficha aún muestra capturas del diseño antiguo. Sustitúyelas por las de
  `docs/screenshots/` y actualiza la descripción con las novedades. Los cambios exigen una cuenta con
  sesión iniciada (*Suggest changes*), así que no se pueden automatizar.
- **Gestores de paquetes**: poder instalar con `winget install` es descubrimiento gratis y de confianza. El
  manifiesto de la versión actual está en `installer/winget/` (validado con `winget validate`). Para
  publicarlo, copia `manifests/` en un fork de [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs)
  y abre un PR; cuando se acepte el primero, cada release es un comando:
  `wingetcreate update JuanP-G.MC-ServerLauncher --version X --urls <url-del-instalador> --submit`.
  Scoop y Chocolatey funcionan de forma parecida.
- **Listados con tráfico propio**: Softpedia, MajorGeeks, SourceForge, Flathub (para el AppImage).
- **Comunidades**: r/admincraft, r/Minecraft (con las normas de autopromoción de cada una), Modrinth.

## Guías para búsquedas concretas

La landing compite por «servidor de Minecraft», una búsqueda muy disputada. Las guías apuntan a
búsquedas largas y específicas, donde una página dedicada puede ganar. Hay tres, cada una en los cinco
idiomas (15 páginas):

| Guía (`id`) | Búsqueda a la que responde | Español |
| --- | --- | --- |
| `mods` | crear un servidor de Minecraft con mods | `/guias/crear-servidor-minecraft-con-mods/` |
| `gratis` | hostear un servidor gratis sin abrir puertos | `/guias/hostear-servidor-minecraft-gratis-sin-abrir-puertos/` |
| `dormir` | servidor que se apaga solo cuando está vacío | `/guias/servidor-minecraft-se-apaga-solo-cuando-esta-vacio/` |

Las demás lenguas viven en `/en/guides/`, `/de/guides/`, `/fr/guides/` y `/pt/guides/`, cada una con su
propio título, descripción, `canonical`, `hreflang` (las cinco versiones más `x-default`) y datos
estructurados (`Article`, `HowTo`, `FAQPage` y `BreadcrumbList`). Google ya no pinta el resultado
enriquecido de `HowTo` y `FAQPage` en la mayoría de sitios, pero los datos siguen ayudando a entender la
página.

**Dónde está el texto.** Cada guía es un único archivo, `web/_i18n/guides/<id>.json`, con los cinco
idiomas dentro; el español marca la estructura (bloques, pasos y preguntas). Las páginas no se editan a
mano. `python web/_i18n/build.py` genera landing, traducciones y guías de una vez, y la plantilla recorta
la hoja de estilos, el escenario, la barra, las descargas y el pie de la propia landing ya traducida, así
que las guías heredan sus cambios y su traducción de la interfaz sin repetirlos.

**Qué se comprueba al generar.** La generación falla, sin escribir nada, si a un idioma le falta una
cadena, si su estructura no coincide con la del español, si algún texto largo es idéntico al español (sin
traducir), si un `title` pasa de 78 caracteres o una `description` se sale de 70–175, o si un enlace
interno `{guide:id}` no existe. También reescribe las entradas de las guías en `sitemap.xml` (entre los
marcadores `guides:start` y `guides:end`) y las tarjetas de la sección «Guías» de la landing.

**Añadir una guía.** Crea `web/_i18n/guides/<id>.json` copiando una existente (slugs, fechas, `icon` y
los cinco idiomas), añade su tarjeta en español a la sección `#guias` de `web/index.html` (el texto de la
tarjeta es `card_title` / `card_desc` y la ruta es la del slug en español) y ejecuta
`python web/_i18n/build.py`. `ping-indexnow.py` la recoge sola. Cambia `modified` cuando la edites de
verdad: es lo que ve el sitemap.

**Tras publicar.** En Search Console, *Inspección de URLs → Solicitar indexación* con las tres direcciones
en español (las demás las descubre por el sitemap y el `hreflang`), y `python web/_i18n/ping-indexnow.py`
para Bing. Espera unas semanas y mira en *Rendimiento* qué consultas traen impresiones a cada guía; en
GoatCounter, qué guía acaba en un clic de descarga.

## Cuando una cifra no cuadra

- GoatCounter suele mostrar menos visitas que otras herramientas porque no cuenta bots ni rastrea a quien
  bloquea scripts de terceros; es lo esperable en una medición que respeta la privacidad.
- Los contadores de descargas incluyen las que haces tú al probar una release.
