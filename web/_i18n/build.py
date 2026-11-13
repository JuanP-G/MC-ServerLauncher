#!/usr/bin/env python3
"""
Genera las versiones traducidas de la landing a partir de web/index.html (español),
que es la única fuente de verdad: se edita ese archivo y este script propaga.

    python web/_i18n/build.py

Salida: web/en/index.html, web/de/index.html, web/fr/index.html, web/pt/index.html
y, a continuación, las guías (web/_i18n/guides.py): web/guias/... y web/<xx>/guides/...

Por qué una URL por idioma y no un selector con JavaScript: los buscadores indexan
el HTML que reciben, así que un cambio de textos en cliente les dejaría una sola
versión. Con una URL por idioma más las etiquetas hreflang, cada versión se indexa
por separado y Google sirve la que corresponde a cada usuario.

El script falla si alguna cadena del diccionario ya no existe en el HTML (señal de
que se cambió el español y se olvidó actualizar la traducción) o si alguna cadena del
diccionario sobrevive sin traducir en la salida. Solo mira las cadenas del diccionario: un
texto en español que se añada al HTML y no esté en él no se detecta, hay que traducirlo a mano
(el CI regenera las páginas y falla si lo generado no coincide con lo publicado).
"""

import hashlib
import json
import pathlib
import re
import sys

import guides      # hermano de este script: genera las guías a partir de la landing ya traducida

BASE_URL = "https://mc-server-launcher.vercel.app"

ROOT = pathlib.Path(__file__).resolve().parent.parent      # web/
SOURCE = ROOT / "index.html"
STRINGS = pathlib.Path(__file__).resolve().parent / "strings.json"

SPANISH = {"lang": "es", "locale": "es_ES", "label": "Español"}


def hreflang_for(code):
    """Etiqueta hreflang de un idioma: la URL de portugués sigue siendo /pt/, pero es portugués de Brasil."""
    return guides.HREFLANG.get(code, code)


def write_manifests(data, problems):
    """Un manifiesto web por idioma, derivado del español (assets/site.webmanifest, escrito a mano).

    Solo cambian la descripción (de strings.json, "manifest_description"), el idioma y la página
    de inicio: abrir la app instalada desde una página inglesa vuelve a /en/ y no a la española.
    Los iconos y los colores son los del original.
    """
    base = json.loads((ROOT / "assets" / "site.webmanifest").read_text(encoding="utf-8"))
    for code, meta in data.items():
        if code.startswith("_"):
            continue
        description = meta.get("manifest_description")
        if not description:
            problems.append("[%s] strings.json no tiene manifest_description" % code)
            continue
        manifest = dict(base)
        manifest["description"] = description
        manifest["lang"] = hreflang_for(code)
        manifest["start_url"] = "../%s/" % code
        target = ROOT / "assets" / guides.manifest_name(code)
        target.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print("%s -> assets/%s" % (code, target.name))


def hreflang_block(languages):
    """Bloque de alternativas que debe ir en TODAS las versiones, la española incluida."""
    lines = ['<link rel="alternate" hreflang="es" href="%s/">' % BASE_URL]
    for code in languages:
        lines.append('<link rel="alternate" hreflang="%s" href="%s/%s/">' % (hreflang_for(code), BASE_URL, code))
    # x-default: la que se sirve a quien no encaje en ningún idioma.
    lines.append('<link rel="alternate" hreflang="x-default" href="%s/">' % BASE_URL)
    return "\n".join(lines)


def rewrite_language_links(html, active):
    """Ajusta el selector de idioma: rutas relativas al subdirectorio y marca el activo.

    Casa cada enlace por su atributo hreflang y no por el texto exacto de la etiqueta: los
    enlaces llevan además atributos de analítica (data-goatcounter-*), y una sustitución de
    texto fijo dejaba sin reescribir las rutas de las versiones traducidas (/en/fr/).
    """
    # Desde /xx/ hay que subir un nivel para llegar a la raíz y a los demás idiomas.
    up = "" if active == "es" else "../"

    def fix(m):
        tag = m.group(3)                 # "pt-BR": el idioma de la ruta es la parte anterior al guion
        code = tag.split("-")[0].lower()
        href = up + ("" if code == "es" else code + "/") or "./"
        rest = m.group(2).replace(' class="on"', "")
        if code == active:
            rest = ' class="on"' + rest
        return '<a href="%s"%s hreflang="%s"' % (href, rest, tag)

    return re.sub(r'<a href="([^"]*)"([^>]*?) hreflang="([\w-]+)"', fix, html)


def localize(html, code, meta, mapping):
    """Aplica el diccionario en una sola pasada y adapta rutas, idioma y direcciones."""
    # Una sola pasada con las claves más largas primero: si se sustituyera clave a clave,
    # "Descargar" rompería "Descargar AppImage", y el texto ya traducido podría volver a
    # coincidir con otra clave.
    keys = sorted(mapping, key=len, reverse=True)
    pattern = re.compile("|".join(re.escape(k) for k in keys))
    out = pattern.sub(lambda m: mapping[m.group(0)], html)

    # Las páginas viven en /xx/, así que las imágenes quedan un nivel por encima.
    out = out.replace('"assets/', '"../assets/')
    out = out.replace(', assets/', ', ../assets/')   # el resto de URLs de un srcset

    # Cada idioma tiene su manifiesto (descripción y página de inicio en ese idioma).
    out = out.replace('href="../assets/site.webmanifest"',
                      'href="../assets/%s"' % guides.manifest_name(code))

    out = out.replace('<html lang="es">', '<html lang="%s">' % meta["lang"])
    out = out.replace('content="es_ES"', 'content="%s"' % meta["locale"])
    out = out.replace('href="%s/"' % BASE_URL, 'href="%s/%s/"' % (BASE_URL, code), 1)   # canonical
    out = out.replace('content="%s/"' % BASE_URL, 'content="%s/%s/"' % (BASE_URL, code))  # og:url
    out = out.replace('"url": "%s/"' % BASE_URL, '"url": "%s/%s/"' % (BASE_URL, code))    # JSON-LD
    return rewrite_language_links(out, code)


# Política de contenido de cada página. Es una etiqueta <meta> porque GitHub Pages no deja poner
# cabeceras; frame-ancestors, report-uri y sandbox no funcionan en una etiqueta, así que aquí no
# se incluyen. Vercel sí las manda: web/vercel.json añade frame-ancestors como cabecera, y se
# suma a esta política en vez de sustituirla.
#
#   script-src   solo el propio sitio. El contador (GoatCounter) ya no se carga de gc.zgo.at: su
#                count.js (ISC, 9 KB, sin dependencias) está copiado en assets/, porque un script de
#                un tercero sin atributo integrity corre con acceso a todo el DOM y esa CDN puede
#                cambiarlo cuando quiera; con integrity, cualquier actualización suya habría roto
#                el contador sin avisar. La página tampoco lleva código en línea (todo está en
#                assets/site.js), así que no hace falta ningún hash y un script inyectado en la
#                página no se ejecuta aunque llegue a escribirse en ella.
#   connect-src  la API de GitHub (el listado de versiones) y el contador.
#   style-src    'unsafe-inline': la página lleva estilos en línea y atributos style; los estilos no
#                ejecutan código, y quitarlos sería reescribir el diseño.
#   base-uri, form-action, object-src, default-src 'none': nada de lo que la página no usa.
CSP = ("default-src 'none'; "
       "script-src 'self'; "
       "connect-src https://api.github.com https://mc-serverlauncher.goatcounter.com; "
       "img-src 'self' data: https://mc-serverlauncher.goatcounter.com; "
       "style-src 'self' 'unsafe-inline'; "
       "font-src 'self'; "
       "manifest-src 'self'; "
       "base-uri 'none'; form-action 'none'; object-src 'none'")

# La 404 (web/404.html) no hace peticiones de red ni lleva analítica: sin GoatCounter, sin la API de
# GitHub y sin manifiesto. Es la política de arriba recortada a lo que esa página usa de verdad.
CSP_404 = ("default-src 'none'; "
           "script-src 'self'; "
           "img-src 'self' data:; "
           "style-src 'self' 'unsafe-inline'; "
           "font-src 'self'; "
           "base-uri 'none'; form-action 'none'; object-src 'none'")

CSP_TAG = re.compile(r'\n?<meta http-equiv="Content-Security-Policy"[^>]*>')


# Un <script> con código escrito dentro. El JSON-LD no es código, y los que llevan src son archivos.
# HTML no distingue mayúsculas: <SCRIPT> también cuenta (y es lo que marcaba CodeQL).
INLINE_SCRIPT = re.compile(r'<script(?![^>]*\bsrc=)(?![^>]*type="application/ld\+json")[^>]*>',
                           re.IGNORECASE)

# Los archivos que cambian con el sitio llevan en la URL un resumen de su contenido: un navegador
# o una caché intermedia que guardó la versión anterior pide la nueva en cuanto cambia.
VERSIONED_ASSETS = ("site.css", "site.js", "guide.css", "404.css", "404.js", "count.js")
VERSIONED_URL = re.compile(r'assets/(%s)(?:\?v=[0-9a-f]+)?"' % "|".join(re.escape(a) for a in VERSIONED_ASSETS))


def asset_versions():
    """Primeros 10 caracteres del sha256 de cada archivo versionado.

    Con los finales de línea normalizados a LF, que es como los guarda git: una copia de trabajo
    de Windows (core.autocrlf) los tiene en CRLF y, sin esto, daría un resumen distinto del que ya
    está en las páginas y reescribiría las 30 solo por haber ejecutado el script."""
    data = lambda name: (ROOT / "assets" / name).read_bytes().replace(b"\r\n", b"\n")
    return {name: hashlib.sha256(data(name)).hexdigest()[:10] for name in VERSIONED_ASSETS}


def stamp_assets(html, versions):
    """Pone (o actualiza) ?v=<resumen> en los enlaces a los archivos versionados."""
    return VERSIONED_URL.sub(lambda m: 'assets/%s?v=%s"' % (m.group(1), versions[m.group(1)]), html)


def apply_csp(html, versions, csp=CSP):
    """Pone (o actualiza) la política de la página y la versión de sus archivos."""
    html = CSP_TAG.sub("", html)
    if INLINE_SCRIPT.search(html):
        sys.exit("ERROR: una página lleva un <script> en línea; la política de contenido no lo permite "
                 "(ponlo en web/assets/site.js)")
    html = stamp_assets(html, versions)
    tag = '<meta http-equiv="Content-Security-Policy" content="%s">' % csp
    if '<meta charset="utf-8">' not in html:
        sys.exit("ERROR: una página no tiene <meta charset> donde poner la política de contenido")
    return html.replace('<meta charset="utf-8">', '<meta charset="utf-8">\n' + tag, 1)


def apply_csp_everywhere():
    """Todas las páginas generadas, la landing española incluida; se ejecuta la última."""
    count = 0
    versions = asset_versions()
    for page in sorted(ROOT.rglob("index.html")):
        if any(part.startswith("_") for part in page.relative_to(ROOT).parts):
            continue
        page.write_text(apply_csp(page.read_text(encoding="utf-8"), versions), encoding="utf-8")
        count += 1
    # La página de error vive en la raíz y se llama 404.html (así la sirven Vercel y GitHub Pages).
    error_page = ROOT / "404.html"
    error_page.write_text(apply_csp(error_page.read_text(encoding="utf-8"), versions, CSP_404), encoding="utf-8")
    count += 1
    print("Content-Security-Policy y versión de los archivos -> %d páginas" % count)


def main():
    source = SOURCE.read_text(encoding="utf-8")
    data = json.loads(STRINGS.read_text(encoding="utf-8"))
    languages = [k for k in data if not k.startswith("_")]

    # El script reescribe su propio archivo fuente (index.html), así que tiene que poder
    # ejecutarse las veces que haga falta sin acumular cambios: primero se borran las
    # etiquetas hreflang que hubiera de una ejecución anterior y luego se ponen de nuevo.
    source = re.sub(r'\n?<link rel="alternate" hreflang="[^"]*" href="[^"]*">', "", source)

    canonical = '<link rel="canonical" href="%s/">' % BASE_URL
    if canonical not in source:
        sys.exit("ERROR: no se encontró la etiqueta canonical en index.html")
    source = source.replace(canonical, canonical + "\n" + hreflang_block(languages), 1)

    problems = []

    # Versión española: misma página, con hreflang y el selector marcado.
    spanish = rewrite_language_links(source, "es")
    SOURCE.write_text(spanish, encoding="utf-8")
    print("es -> index.html")

    for code in languages:
        meta = data[code]
        # Las tarjetas de guía de la landing se traducen con los textos de las propias guías,
        # para que el título de la tarjeta y el de la guía no puedan diverger.
        mapping = dict(meta["strings"])
        mapping.update(guides.landing_strings(code))

        missing = [k for k in mapping if k not in source]
        if missing:
            problems.append("[%s] %d cadenas del diccionario ya no están en el HTML español:\n    - %s"
                            % (code, len(missing), "\n    - ".join(m[:70] for m in missing)))

        out = localize(source, code, meta, mapping)
        for es_href, local_href in guides.landing_hrefs(code).items():
            if es_href not in out:
                problems.append("[%s] la landing no enlaza a la guía %s" % (code, es_href))
            out = out.replace(es_href, local_href)

        # Comprobación de que no queda español: ninguna clave traducida puede sobrevivir.
        leftovers = [k for k, v in mapping.items() if v != k and k in out]
        if leftovers:
            problems.append("[%s] %d cadenas siguen en español en la salida:\n    - %s"
                            % (code, len(leftovers), "\n    - ".join(l[:70] for l in leftovers)))

        # Los datos estructurados se inyectan dentro de bloques JSON, así que una comilla
        # recta en una traducción los rompe en silencio y Google descarta el resultado
        # enriquecido. Se validan aquí para que el fallo salte en la generación.
        for i, block in enumerate(re.findall(r'<script type="application/ld\+json">(.*?)</script>',
                                             out, re.DOTALL), 1):
            try:
                json.loads(block)
            except json.JSONDecodeError as err:
                problems.append("[%s] el bloque JSON-LD nº%d no es válido: %s\n"
                                "    Suele ser una comilla recta (\") en alguna traducción; "
                                "usa comillas tipográficas." % (code, i, err))

        target = ROOT / code / "index.html"
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(out, encoding="utf-8")
        print("%s -> %s/index.html  (%d cadenas)" % (code, code, len(mapping)))

    write_manifests(data, problems)

    if problems:
        print("\n".join(problems), file=sys.stderr)
        sys.exit("\nLa generación ha fallado: corrige lo anterior.")
    print("\nTodas las versiones generadas y verificadas.\n")

    # Las guías cortan su estilo y su barra de la landing recién generada, así que van después.
    guides.main()

    # Lo último: la política y la versión de los archivos, con las páginas ya definitivas.
    apply_csp_everywhere()


if __name__ == "__main__":
    main()
