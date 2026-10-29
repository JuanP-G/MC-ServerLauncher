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
que se cambió el español y se olvidó actualizar la traducción) o si queda texto en
español sin traducir en la salida.
"""

import base64
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

    out = out.replace('<html lang="es">', '<html lang="%s">' % meta["lang"])
    out = out.replace('content="es_ES"', 'content="%s"' % meta["locale"])
    out = out.replace('href="%s/"' % BASE_URL, 'href="%s/%s/"' % (BASE_URL, code), 1)   # canonical
    out = out.replace('content="%s/"' % BASE_URL, 'content="%s/%s/"' % (BASE_URL, code))  # og:url
    out = out.replace('"url": "%s/"' % BASE_URL, '"url": "%s/%s/"' % (BASE_URL, code))    # JSON-LD
    return rewrite_language_links(out, code)


# Política de contenido de cada página. Es una etiqueta <meta> porque el hosting no se configura
# desde el repositorio; frame-ancestors, report-uri y sandbox no funcionan en una etiqueta, así que
# no se incluyen.
#
#   script-src   el propio sitio, el contador (GoatCounter) y el script en línea, por su hash: así
#                un script inyectado en la página no se ejecuta aunque llegue a escribirse en ella.
#   connect-src  la API de GitHub (el listado de versiones) y el contador.
#   style-src    'unsafe-inline': la página lleva estilos en línea y atributos style; los estilos no
#                ejecutan código, y quitarlos sería reescribir el diseño.
#   base-uri, form-action, object-src, default-src 'none': nada de lo que la página no usa.
CSP = ("default-src 'none'; "
       "script-src 'self' {hashes} https://gc.zgo.at; "
       "connect-src https://api.github.com https://mc-serverlauncher.goatcounter.com; "
       "img-src 'self' data: https://mc-serverlauncher.goatcounter.com https://gc.zgo.at; "
       "style-src 'self' 'unsafe-inline'; "
       "font-src 'self'; "
       "manifest-src 'self'; "
       "base-uri 'none'; form-action 'none'; object-src 'none'")

CSP_TAG = re.compile(r'\n?<meta http-equiv="Content-Security-Policy"[^>]*>')


def inline_script_hashes(html):
    """Hash (sha256, base64) de cada <script> en línea. El JSON-LD no es código y no se cuenta."""
    hashes = []
    for body in re.findall(r"<script>(.*?)</script>", html, re.DOTALL):
        digest = base64.b64encode(hashlib.sha256(body.encode("utf-8")).digest()).decode("ascii")
        hashes.append("'sha256-%s'" % digest)
    return hashes


def apply_csp(html):
    """Pone (o actualiza) la política de la página. El hash depende del script de cada versión."""
    html = CSP_TAG.sub("", html)
    tag = '<meta http-equiv="Content-Security-Policy" content="%s">' % CSP.format(
        hashes=" ".join(inline_script_hashes(html)))
    if '<meta charset="utf-8">' not in html:
        sys.exit("ERROR: una página no tiene <meta charset> donde poner la política de contenido")
    return html.replace('<meta charset="utf-8">', '<meta charset="utf-8">\n' + tag, 1)


def apply_csp_everywhere():
    """Todas las páginas generadas, la landing española incluida; se ejecuta la última."""
    count = 0
    for page in sorted(ROOT.rglob("index.html")):
        if any(part.startswith("_") for part in page.relative_to(ROOT).parts):
            continue
        page.write_text(apply_csp(page.read_text(encoding="utf-8")), encoding="utf-8")
        count += 1
    print("Content-Security-Policy -> %d páginas" % count)


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

    if problems:
        print("\n".join(problems), file=sys.stderr)
        sys.exit("\nLa generación ha fallado: corrige lo anterior.")
    print("\nTodas las versiones generadas y verificadas.\n")

    # Las guías cortan su estilo y su barra de la landing recién generada, así que van después.
    guides.main()

    # Lo último: el hash del script de cada página es el de su texto ya traducido y definitivo.
    apply_csp_everywhere()


if __name__ == "__main__":
    main()
