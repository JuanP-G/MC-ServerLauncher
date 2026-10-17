# Visibilidad y estadísticas

> 🇬🇧 Read this in [English](visibility.md).

Cómo se mide quién llega a la web y quién descarga la app, y dónde conviene estar para que más gente
la encuentre. La app no lleva telemetría y no se va a añadir sin que sea opcional y explícita: aquí solo se
mide la web y lo que GitHub ya publica.

## Qué se mide y con qué

| Qué | Herramienta | Qué guarda |
| --- | --- | --- |
| Visitas a la web, país, idioma, dispositivo, de dónde llegan | [GoatCounter](https://www.goatcounter.com) | Sin cookies ni banner. No guarda la IP. Gratis para proyectos sin ánimo de lucro. |
| Clics en Descargar (por sistema), GitHub, Issues, cambio de idioma | Eventos de GoatCounter (`data-goatcounter-click` en `web/index.html`) | Un contador por botón, sin datos personales. |
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
- **AlternativeTo**: la ficha no tiene ninguna captura de pantalla. Sube las de `docs/screenshots/`
  (o unas nuevas, ver abajo) y la descripción con las novedades. Es lo que más cambia cómo se ve la ficha.
- **Gestores de paquetes**: poder instalar con `winget install` es descubrimiento gratis y de confianza. El
  manifiesto de la versión actual está en `installer/winget/` (validado con `winget validate`). Para
  publicarlo, copia `manifests/` en un fork de [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs)
  y abre un PR; cuando se acepte el primero, cada release es un comando:
  `wingetcreate update JuanP-G.MC-ServerLauncher --version X --urls <url-del-instalador> --submit`.
  Scoop y Chocolatey funcionan de forma parecida.
- **Listados con tráfico propio**: Softpedia, MajorGeeks, SourceForge, Flathub (para el AppImage).
- **Comunidades**: r/admincraft, r/Minecraft (con las normas de autopromoción de cada una), Modrinth.

## Cuando una cifra no cuadra

- GoatCounter suele mostrar menos visitas que otras herramientas porque no cuenta bots ni rastrea a quien
  bloquea scripts de terceros; es lo esperable en una medición que respeta la privacidad.
- Los contadores de descargas incluyen las que haces tú al probar una release.
