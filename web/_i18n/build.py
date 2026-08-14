#!/usr/bin/env python3
"""
Genera las versiones traducidas de la landing a partir de web/index.html (español),
que es la única fuente de verdad: se edita ese archivo y este script propaga.

    python web/_i18n/build.py

Salida: web/en/index.html, web/de/index.html, web/fr/index.html, web/pt/index.html

Por qué una URL por idioma y no un selector con JavaScript: los buscadores indexan
el HTML que reciben, así que un cambio de textos en cliente les dejaría una sola
versión. Con una URL por idioma más las etiquetas hreflang, cada versión se indexa
por separado y Google sirve la que corresponde a cada usuario.

El script falla si alguna cadena del diccionario ya no existe en el HTML (señal de
que se cambió el español y se olvidó actualizar la traducción) o si queda texto en
español sin traducir en la salida.
"""

import json
import pathlib
import re
import sys

BASE_URL = "https://juanp-g.github.io/MC-ServerLauncher"

ROOT = pathlib.Path(__file__).resolve().parent.parent      # web/
SOURCE = ROOT / "index.html"
STRINGS = pathlib.Path(__file__).resolve().parent / "strings.json"

SPANISH = {"lang": "es", "locale": "es_ES", "label": "Español"}


def hreflang_block(languages):
    """Bloque de alternativas que debe ir en TODAS las versiones, la española incluida."""
    lines = ['<link rel="alternate" hreflang="es" href="%s/">' % BASE_URL]
    for code in languages:
        lines.append('<link rel="alternate" hreflang="%s" href="%s/%s/">' % (code, BASE_URL, code))
    # x-default: la que se sirve a quien no encaje en ningún idioma.
    lines.append('<link rel="alternate" hreflang="x-default" href="%s/">' % BASE_URL)
    return "\n".join(lines)


def rewrite_language_links(html, active):
    """Ajusta el selector de idioma: rutas relativas al subdirectorio y marca el activo."""
    html = html.replace(' class="on" hreflang="es"', ' hreflang="es"')
    if active != "es":
        # Desde /xx/ hay que subir un nivel para llegar a la raíz y a los demás idiomas.
        html = html.replace('<a href="./" hreflang="es"', '<a href="../" hreflang="es"')
        for code in ("en", "de", "fr", "pt"):
            html = html.replace('<a href="%s/" hreflang="%s"' % (code, code),
                                '<a href="../%s/" hreflang="%s"' % (code, code))
        target = '<a href="../%s/" hreflang="%s"' % (active, active)
    else:
        target = '<a href="./" hreflang="es"'
    return html.replace(target, target.replace(' hreflang=', ' class="on" hreflang='), 1)


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

    out = out.replace('<html lang="es">', '<html lang="%s">' % meta["lang"])
    out = out.replace('content="es_ES"', 'content="%s"' % meta["locale"])
    out = out.replace('href="%s/"' % BASE_URL, 'href="%s/%s/"' % (BASE_URL, code), 1)   # canonical
    out = out.replace('content="%s/"' % BASE_URL, 'content="%s/%s/"' % (BASE_URL, code))  # og:url
    out = out.replace('"url": "%s/"' % BASE_URL, '"url": "%s/%s/"' % (BASE_URL, code))    # JSON-LD
    return rewrite_language_links(out, code)


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
        mapping = meta["strings"]

        missing = [k for k in mapping if k not in source]
        if missing:
            problems.append("[%s] %d cadenas del diccionario ya no están en el HTML español:\n    - %s"
                            % (code, len(missing), "\n    - ".join(m[:70] for m in missing)))

        out = localize(source, code, meta, mapping)

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
    print("\nTodas las versiones generadas y verificadas.")


if __name__ == "__main__":
    main()
