#!/usr/bin/env python3
"""
Genera las guías de la web (cola larga de búsquedas) en los cinco idiomas a partir de
una sola fuente por guía: web/_i18n/guides/<id>.json, con el español como referencia
estructural y las demás lenguas debajo.

    python web/_i18n/guides.py        # solo las guías (necesita las landings ya generadas)
    python web/_i18n/build.py         # landing + traducciones + guías, todo de una vez

Salida: web/guias/<slug>/index.html (español) y web/<xx>/guides/<slug>/index.html.
También reescribe las entradas de las guías en web/sitemap.xml (entre los marcadores
guides:start y guides:end).

Cómo se reutiliza el estilo sin duplicarlo: la hoja de estilos, el escenario, la barra, la
sección de descargas, el pie y el script se recortan de la landing YA traducida de cada
idioma (web/index.html, web/en/index.html...). Así una guía hereda cualquier cambio de la
landing en la siguiente generación, y los textos de la interfaz (menú, descargas, pie)
salen traducidos sin repetirlos aquí. Solo lo que es propio de la guía vive en este módulo.

Falla (y no escribe nada a medias) si a algún idioma le falta una cadena, si la estructura
de una traducción no coincide con la del español, si queda un texto idéntico al español
sin traducir, o si algún enlace interno no se resuelve.
"""

import html
import json
import pathlib
import posixpath
import re
import sys

BASE_URL = "https://mc-server-launcher.vercel.app"
LANGS = ["es", "en", "de", "fr", "pt"]
LOCALES = {"es": "es_ES", "en": "en_US", "de": "de_DE", "fr": "fr_FR", "pt": "pt_PT"}

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parent                      # web/
GUIDES_DIR = HERE / "guides"
UI_FILE = GUIDES_DIR / "_ui.json"
SITEMAP = ROOT / "sitemap.xml"

# Cadenas que pueden ser idénticas al español sin que sea un olvido (nombres propios, cifras).
SAME_OK_MAX_LEN = 24


def fail(msg):
    sys.exit("ERROR en las guías: " + msg)


# ---------------------------------------------------------------- rutas y direcciones

def landing_path(code):
    return "" if code == "es" else code + "/"


def guide_path(code, slug):
    """Ruta (desde la raíz del sitio, con barra final) de una guía en un idioma."""
    return "guias/%s/" % slug if code == "es" else "%s/guides/%s/" % (code, slug)


def rel(src, dst):
    """Enlace relativo de una página (directorio) a otra, como el resto de la web."""
    s, d = src.strip("/"), dst.strip("/")
    r = posixpath.relpath(d or ".", s or ".")
    return "./" if r == "." else r + "/"


def up_prefix(path):
    """Prefijo para subir de una página hasta la raíz (donde vive assets/)."""
    return "../" * len([p for p in path.split("/") if p])


# ---------------------------------------------------------------- carga y validación

def load():
    if not UI_FILE.exists():
        fail("falta " + str(UI_FILE))
    ui = json.loads(UI_FILE.read_text(encoding="utf-8"))
    guides = {}
    for f in sorted(GUIDES_DIR.glob("*.json")):
        if f.name.startswith("_"):
            continue
        guides[f.stem] = json.loads(f.read_text(encoding="utf-8"))
    if not guides:
        fail("no hay ninguna guía en " + str(GUIDES_DIR))
    return ui, guides


def shape(node):
    """Forma de un contenido con los textos vaciados: lo que debe coincidir entre idiomas."""
    if isinstance(node, dict):
        return {k: shape(v) for k, v in node.items()}
    if isinstance(node, list):
        return [shape(v) for v in node]
    return "s"


def leaves(node, path=""):
    if isinstance(node, dict):
        for k, v in node.items():
            yield from leaves(v, "%s.%s" % (path, k))
    elif isinstance(node, list):
        for i, v in enumerate(node):
            yield from leaves(v, "%s[%d]" % (path, i))
    else:
        yield path, node


def validate(ui, guides):
    problems = []

    for code in LANGS:
        if code not in ui:
            problems.append("_ui.json: falta el idioma %s" % code)
    if not problems:
        base = shape(ui["es"])
        for code in LANGS[1:]:
            if shape(ui[code]) != base:
                problems.append("_ui.json[%s]: las claves no coinciden con las del español" % code)

    slugs_seen = {}
    for gid, g in guides.items():
        for code in LANGS:
            if code not in g or code not in g.get("slug", {}):
                problems.append("[%s] falta el idioma %s (contenido o slug)" % (gid, code))
        if any(c not in g or c not in g.get("slug", {}) for c in LANGS):
            continue
        for key in ("published", "modified", "minutes", "howto_minutes", "icon"):
            if key not in g:
                problems.append("[%s] falta el campo %s" % (gid, key))
        base = shape(g["es"])
        for code in LANGS:
            slug = g["slug"][code]
            if not re.fullmatch(r"[a-z0-9]+(-[a-z0-9]+)*", slug):
                problems.append("[%s/%s] slug no válido: %r" % (gid, code, slug))
            if (code, slug) in slugs_seen:
                problems.append("[%s/%s] slug repetido con %s" % (gid, code, slugs_seen[(code, slug)]))
            slugs_seen[(code, slug)] = gid
            if code != "es" and shape(g[code]) != base:
                problems.append("[%s/%s] la estructura no coincide con la del español "
                                "(mismos bloques, mismos pasos, mismas preguntas)" % (gid, code))
            for path, text in leaves(g[code]):
                if not isinstance(text, str) or not text.strip():
                    problems.append("[%s/%s] cadena vacía en %s" % (gid, code, path))
                elif code != "es" and len(text) > SAME_OK_MAX_LEN:
                    es_text = dict(leaves(g["es"])).get(path)
                    if es_text == text:
                        problems.append("[%s/%s] sin traducir (igual que el español) en %s"
                                        % (gid, code, path))
            title = g[code].get("title", "")
            if len(title) > 78:
                problems.append("[%s/%s] el title mide %d (>78): Google lo corta" % (gid, code, len(title)))
            desc = g[code].get("description", "")
            if not 70 <= len(desc) <= 175:
                problems.append("[%s/%s] la description mide %d (debe estar entre 70 y 175)"
                                % (gid, code, len(desc)))
    if problems:
        print("\n".join(problems), file=sys.stderr)
        fail("%d problemas; corrígelos y vuelve a ejecutar." % len(problems))


# ---------------------------------------------------------------- piezas de HTML

def esc(text):
    return html.escape(text, quote=True)


def plain(text):
    """Texto sin etiquetas, para los datos estructurados y las metaetiquetas."""
    return html.unescape(re.sub(r"<[^>]+>", "", text))


def resolve_links(text, code, gid, guides, src_path):
    """Sustituye {home} y {guide:id} por enlaces relativos desde la página que se genera."""
    def one(m):
        token = m.group(1)
        if token == "home":
            return rel(src_path, landing_path(code))
        if token.startswith("guide:"):
            target = token[6:]
            if target not in guides:
                fail("[%s/%s] enlace a una guía que no existe: %s" % (gid, code, target))
            return rel(src_path, guide_path(code, guides[target]["slug"][code]))
        fail("[%s/%s] marcador desconocido: {%s}" % (gid, code, token))
    out = re.sub(r"\{([a-z]+(?::[a-z0-9-]+)?)\}", one, text)
    if "{" in out or "}" in out:
        fail("[%s/%s] llave sin resolver en: %s" % (gid, code, out[:80]))
    return out


def format_date(iso, ui_lang):
    y, m, d = iso.split("-")
    return ui_lang["date"].format(d=int(d), month=ui_lang["months"][int(m) - 1], y=y)


def render_blocks(blocks, ui_lang, code, gid, guides, src_path):
    r = lambda t: resolve_links(t, code, gid, guides, src_path)
    out = []
    for b in blocks:
        (kind, val), = b.items()
        if kind == "h2":
            out.append('<h2 class="g-h2">%s</h2>' % r(val))
        elif kind == "p":
            out.append("<p>%s</p>" % r(val))
        elif kind == "ul":
            out.append("<ul>%s</ul>" % "".join("<li>%s</li>" % r(i) for i in val))
        elif kind == "tip":
            out.append('<aside class="tip"><strong>%s</strong><p>%s</p></aside>'
                       % (esc(ui_lang["tip"]), r(val)))
        elif kind == "steps":
            items = "".join(
                '<li><b class="n" aria-hidden="true">%d</b><div><h3>%s</h3><p>%s</p></div></li>'
                % (i, r(s["name"]), r(s["text"])) for i, s in enumerate(val, 1))
            out.append('<ol class="steps">%s</ol>' % items)
        else:
            fail("[%s/%s] tipo de bloque desconocido: %s" % (gid, code, kind))
    return "\n".join(out)


def json_ld(data):
    return '<script type="application/ld+json">\n%s\n</script>' % json.dumps(data, ensure_ascii=False, indent=2)


def structured_data(gid, g, code, ui_lang, guides, url, src_path):
    content = g[code]
    steps = next(b["steps"] for b in content["blocks"] if "steps" in b)
    resolve = lambda t: plain(resolve_links(t, code, gid, guides, src_path))
    image = "%s/assets/og/%s.jpg" % (BASE_URL, code)
    author = {"@type": "Person", "name": "JuanP-G", "url": "https://github.com/JuanP-G"}
    return [
        {
            "@context": "https://schema.org", "@type": "Article",
            "headline": content["h1"], "description": content["description"],
            "inLanguage": code, "url": url, "mainEntityOfPage": url, "image": image,
            "datePublished": g["published"], "dateModified": g["modified"],
            "author": author,
            "publisher": {"@type": "Organization", "name": "MC-ServerLauncher", "url": BASE_URL + "/"},
        },
        {
            "@context": "https://schema.org", "@type": "HowTo",
            "name": content["h1"], "description": content["description"], "inLanguage": code,
            "totalTime": "PT%dM" % g["howto_minutes"],
            "estimatedCost": {"@type": "MonetaryAmount", "currency": "EUR", "value": "0"},
            "tool": [{"@type": "HowToTool", "name": "MC-ServerLauncher"}],
            "step": [{"@type": "HowToStep", "position": i, "name": resolve(s["name"]),
                      "text": resolve(s["text"])} for i, s in enumerate(steps, 1)],
        },
        {
            "@context": "https://schema.org", "@type": "FAQPage", "inLanguage": code,
            "mainEntity": [{"@type": "Question", "name": resolve(q["q"]),
                            "acceptedAnswer": {"@type": "Answer", "text": resolve(q["a"])}}
                           for q in content["faq"]],
        },
        {
            "@context": "https://schema.org", "@type": "BreadcrumbList",
            "itemListElement": [
                {"@type": "ListItem", "position": 1, "name": ui_lang["home"],
                 "item": "%s/%s" % (BASE_URL, landing_path(code))},
                {"@type": "ListItem", "position": 2, "name": ui_lang["guides"],
                 "item": "%s/%s#guias" % (BASE_URL, landing_path(code))},
                {"@type": "ListItem", "position": 3, "name": content["h1"], "item": url},
            ],
        },
    ]


# CSS propio de las guías; se añade al de la landing (que ya trae el tema y los componentes).
GUIDE_CSS = """
/* ---------- GUÍAS ---------- */
/* Texto largo sobre el cielo: va en un panel oscuro como el chat del juego, para que se lea
   igual de día que de noche. Los títulos y los números de paso conservan la letra del juego. */
.guide-head{padding-block:clamp(28px,6vh,56px) 0}
.crumbs{display:flex; flex-wrap:wrap; gap:4px 10px; align-items:center; margin-bottom:22px; font-size:14px; color:#CBD8E8;
  text-shadow:0 1px 3px rgba(3,8,16,.9)}
.crumbs a:hover{color:var(--mc-hover)}
.crumbs [aria-current]{color:#fff}
.g-title{font-family:var(--f-mc); font-weight:400; font-size:24px; line-height:1.3; margin:0 0 20px; max-width:980px;
  text-shadow:3px 3px 0 var(--mc-shadow), 0 2px 24px rgba(3,8,16,.6); -webkit-font-smoothing:none; text-wrap:balance}
.guide-head .lead{max-width:760px}
.g-meta{margin:18px 0 26px; color:#CBD8E8; font-size:14px; text-shadow:0 1px 3px rgba(3,8,16,.9)}
.prose{max-width:820px; margin:clamp(28px,5vh,48px) auto 0; padding:clamp(22px,4vw,44px); text-align:left;
  background:rgba(0,0,0,.62); backdrop-filter:blur(8px); -webkit-backdrop-filter:blur(8px);
  box-shadow:inset 0 0 0 2px #2A2A2A, 0 0 0 2px #000, 0 18px 50px rgba(0,0,0,.45)}
.prose p,.prose li{color:#D5DEEA; font-size:17px; line-height:1.72}
.prose p{margin:0 0 16px}
.prose a{color:var(--bright); text-decoration:underline; text-underline-offset:3px}
.prose a:hover{color:var(--mc-hover)}
.prose strong{color:#fff}
.prose code{padding:2px 6px; background:rgba(255,255,255,.1); font-size:.92em}
.prose ul{margin:0 0 18px; padding-left:22px}
.prose li{margin-bottom:8px}
.g-h2{font-family:var(--f-mc); font-weight:400; font-size:24px; line-height:1.3; margin:40px 0 16px;
  text-shadow:3px 3px 0 var(--mc-shadow); -webkit-font-smoothing:none; text-wrap:balance}
.prose > .g-h2:first-child{margin-top:0}
.steps{list-style:none; margin:8px 0 28px; padding:0; display:grid; gap:18px}
.steps li{display:grid; grid-template-columns:auto minmax(0,1fr); gap:16px; margin:0}
.steps .n{display:grid; place-items:center; width:48px; height:48px; margin-top:2px; color:#fff; background:#3C8527;
  font-family:var(--f-mc); font-weight:400; font-size:24px; text-shadow:2px 2px 0 #1D4D13; -webkit-font-smoothing:none;
  box-shadow:inset 0 -4px 0 #1D4D13, inset 3px 3px 0 rgba(255,255,255,.22), 0 0 0 2px #000}
.steps h3{font-size:18px; font-weight:700; line-height:1.35; margin:0 0 6px; color:#fff}
.steps p{margin:0}
.tip{margin:22px 0; padding:16px 20px; color:var(--gui-text); background:var(--gui);
  box-shadow:inset 4px 4px 0 var(--gui-hi), inset -4px -4px 0 var(--gui-lo), 0 0 0 2px #000}
.tip strong{display:block; margin-bottom:4px; color:#404040; font-family:var(--f-mc); font-weight:400; font-size:16px; -webkit-font-smoothing:none}
.tip p{margin:0; color:#3F3F3F; font-size:15px; line-height:1.6}
.tip a{color:#1B5E20}
.guide-faq,.guide-more{padding-block:clamp(40px,8vh,72px) 0}
a.card{color:var(--gui-text)}
@media (min-width:700px){ .g-title{font-size:32px} .g-h2{font-size:24px} }
@media (min-width:1300px) and (min-height:820px){ .g-title{font-size:40px} }
@media (max-width:640px){ .steps li{grid-template-columns:1fr; gap:10px} .prose p,.prose li{font-size:16px} }
"""


def cut_chrome(landing):
    """Recorta de la landing ya traducida las piezas que comparte con las guías."""
    def between(start, end, what, include_end=False):
        i = landing.find(start)
        j = landing.find(end, i + len(start)) if i >= 0 else -1
        if i < 0 or j < 0:
            fail("no se encontró %s en la landing: ¿ha cambiado su estructura?" % what)
        return landing[i:j + (len(end) if include_end else 0)]

    return {
        "css": between("<style>", "</style>", "la hoja de estilos", True),
        "noscript": between("<noscript>", "</noscript>", "el bloque noscript", True),
        "top": between("<body>", "<main>", "la cabecera")[len("<body>"):],
        "downloads": between('<section id="descargas">', "</section>", "la sección de descargas", True),
        "tail": landing[landing.index("<footer>"):],
    }


def fix_assets(text, up):
    return re.sub(r'"(?:\.\./)?assets/', '"%sassets/' % up, text)


def build_page(gid, g, code, ui, guides, chrome):
    ui_lang = ui[code]
    content = g[code]
    slug = g["slug"][code]
    path = guide_path(code, slug)
    url = "%s/%s" % (BASE_URL, path)
    up = up_prefix(path)
    home = rel(path, landing_path(code))
    r = lambda t: resolve_links(t, code, gid, guides, path)

    # Barra: marca y secciones de la landing apuntan a la landing; descargas y FAQ son de la guía.
    top = chrome["top"]
    top = top.replace('href="#inicio"', 'href="%s"' % home)
    for anchor in ("caracteristicas", "como-funciona"):
        top = top.replace('href="#%s"' % anchor, 'href="%s#%s"' % (home, anchor))

    def lang_href(m):
        target = m.group(3)
        return '<a href="%s"%s hreflang="%s"' % (
            rel(path, guide_path(target, g["slug"][target])), m.group(2), target)
    top = re.sub(r'<a href="([^"]*)"([^>]*?) hreflang="(\w\w)"', lang_href, top)

    downloads = re.sub(r'data-goatcounter-click="(descarga-\w+|historial-releases)"',
                       r'data-goatcounter-click="guia-%s-\1"' % gid, chrome["downloads"])

    alternates = "\n".join(
        '<link rel="alternate" hreflang="%s" href="%s/%s">' % (c, BASE_URL, guide_path(c, g["slug"][c]))
        for c in LANGS)
    alternates += '\n<link rel="alternate" hreflang="x-default" href="%s/%s">' % (
        BASE_URL, guide_path("es", g["slug"]["es"]))

    og_image = "%s/assets/og/%s.jpg" % (BASE_URL, code)
    ld = "\n".join(json_ld(d) for d in structured_data(gid, g, code, ui_lang, guides, url, path))
    desc = esc(content["description"])

    head = """<!doctype html>
<html lang="%(code)s">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<!-- Página generada por web/_i18n/guides.py a partir de web/_i18n/guides/%(gid)s.json.
     No se edita a mano: se cambia la fuente y se vuelve a ejecutar python web/_i18n/build.py. -->
<title>%(title)s</title>
<meta name="description" content="%(desc)s">
<link rel="canonical" href="%(url)s">
%(alternates)s
<meta name="theme-color" content="#070B14">
<meta name="robots" content="index, follow, max-image-preview:large">
<link rel="icon" href="%(up)sassets/icons/favicon-32.png" type="image/png" sizes="32x32">
<link rel="icon" href="%(up)sassets/icons/icon-192.png" type="image/png" sizes="192x192">
<link rel="apple-touch-icon" href="%(up)sassets/icons/apple-touch-icon.png">
<link rel="manifest" href="%(up)sassets/site.webmanifest">
<meta name="author" content="JuanP-G">

<meta property="og:type" content="article">
<meta property="og:locale" content="%(locale)s">
<meta property="og:site_name" content="MC-ServerLauncher">
<meta property="og:title" content="%(h1)s">
<meta property="og:description" content="%(desc)s">
<meta property="og:url" content="%(url)s">
<meta property="og:image" content="%(og)s">
<meta property="og:image:type" content="image/jpeg">
<meta property="og:image:width" content="1200">
<meta property="og:image:height" content="630">
<meta property="article:published_time" content="%(pub)s">
<meta property="article:modified_time" content="%(mod)s">
<meta name="twitter:card" content="summary_large_image">
<meta name="twitter:title" content="%(h1)s">
<meta name="twitter:description" content="%(desc)s">
<meta name="twitter:image" content="%(og)s">

<link rel="preload" as="font" type="font/woff2" href="%(up)sassets/fonts/inter-latin.woff2" crossorigin>
<link rel="preload" as="font" type="font/woff" href="%(up)sassets/mc/minecraft.woff" crossorigin>
<link rel="preload" as="image" href="%(up)sassets/mc/button_hover.png">

%(ld)s

""" % {"code": code, "gid": gid, "title": esc(content["title"]), "desc": desc, "url": url,
       "alternates": alternates, "up": up, "locale": LOCALES[code], "h1": esc(content["h1"]),
       "og": og_image, "pub": g["published"], "mod": g["modified"], "ld": ld}

    css = chrome["css"].replace("</style>", GUIDE_CSS + "</style>")
    css = fix_assets(css, up)

    # Cabecera de la guía: ruta de navegación, titular, entradilla, fecha y botón de descarga.
    meta = "%s · %s" % (ui_lang["updated"].format(date=format_date(g["modified"], ui_lang)),
                        ui_lang["read"].format(n=g["minutes"]))
    cta_attrs = 'data-goatcounter-click="guia-%s-cta" data-goatcounter-title="Guía %s: descargar"' % (gid, gid)
    header = """<main>
  <section class="guide-head">
    <div class="wrap">
      <nav class="crumbs" aria-label="%(crumbs)s">
        <a href="%(home)s">%(home_l)s</a><span aria-hidden="true">›</span>
        <a href="%(home)s#guias">%(guides_l)s</a><span aria-hidden="true">›</span>
        <span aria-current="page">%(h1s)s</span>
      </nav>
      <span class="eyebrow">%(eyebrow)s</span>
      <h1 class="g-title">%(h1)s</h1>
      <p class="lead">%(lead)s</p>
      <p class="g-meta">%(meta)s</p>
      <a class="btn btn-primary btn-lg" %(cta_attrs)s href="#descargas">%(cta)s</a>
    </div>
  </section>

  <section class="guide-body">
    <div class="wrap">
      <article class="prose">
%(body)s
      </article>
    </div>
  </section>
""" % {"crumbs": esc(ui_lang["crumbs"]), "home": home, "home_l": esc(ui_lang["home"]),
       "guides_l": esc(ui_lang["guides"]), "h1s": esc(content["crumb"]), "eyebrow": esc(ui_lang["guide"]),
       "h1": r(content["h1"]), "lead": r(content["lead"]), "meta": esc(meta), "cta_attrs": cta_attrs,
       "cta": esc(content["cta"]),
       "body": render_blocks(content["blocks"], ui_lang, code, gid, guides, path)}

    faq = "\n".join(
        '        <details><summary>%s</summary><p>%s</p></details>' % (r(q["q"]), r(q["a"]))
        for q in content["faq"])
    faq_section = """
  <section class="guide-faq" id="faqs">
    <div class="wrap center">
      <span class="eyebrow">%s</span>
      <h2 class="h2">%s</h2>
      <div class="faq">
%s
      </div>
    </div>
  </section>
""" % (esc(ui_lang["faq_eyebrow"]), esc(ui_lang["faq_title"]), faq)

    # Otras guías: enlaces entre guías, que es lo que hace de ellas una red y no páginas sueltas.
    cards = []
    for oid, og in guides.items():
        if oid == gid:
            continue
        iso = ' class="iso"' if og["icon"].startswith("iso_") else ""
        cards.append(
            '<a class="card" data-goatcounter-click="guia-%s-mas-%s" data-goatcounter-title="Guía %s: más %s" href="%s">'
            '<div class="slot"><img%s src="assets/mc/%s" alt="" width="48" height="48"></div>'
            '<h3>%s</h3><p>%s</p></a>'
            % (gid, oid, gid, oid, rel(path, guide_path(code, og["slug"][code])), iso, og["icon"].replace(".png", ".webp"),
               html.escape(og[code]["card_title"]), html.escape(og[code]["card_desc"])))
    more = """
  <section class="guide-more">
    <div class="wrap center">
      <span class="eyebrow">%s</span>
      <h2 class="h2">%s</h2>
      <div class="grid g3">
        %s
      </div>
    </div>
  </section>
""" % (esc(ui_lang["more_eyebrow"]), esc(ui_lang["more_title"]), "\n        ".join(cards))
    more = fix_assets(more, up)

    page = (head + css + "\n" + chrome["noscript"] + "\n\n</head>\n<body>\n" + fix_assets(top, up)
            + header + faq_section + more + "\n" + fix_assets(downloads, up) + "\n</main>\n\n"
            + fix_assets(chrome["tail"], up))
    return path, page


# ---------------------------------------------------------------- sitemap, landing, IndexNow

def all_urls():
    """Todas las direcciones de las guías, para IndexNow y el sitemap."""
    _, guides = load()
    return ["%s/%s" % (BASE_URL, guide_path(c, g["slug"][c])) for g in guides.values() for c in LANGS]


def sitemap_block(guides):
    entries = []
    for gid, g in guides.items():
        alts = "\n".join(
            '    <xhtml:link rel="alternate" hreflang="%s" href="%s/%s"/>' % (c, BASE_URL, guide_path(c, g["slug"][c]))
            for c in LANGS)
        alts += '\n    <xhtml:link rel="alternate" hreflang="x-default" href="%s/%s"/>' % (
            BASE_URL, guide_path("es", g["slug"]["es"]))
        for c in LANGS:
            entries.append("""  <url>
    <loc>%s/%s</loc>
    <lastmod>%s</lastmod>
    <changefreq>monthly</changefreq>
    <priority>%s</priority>
%s
  </url>""" % (BASE_URL, guide_path(c, g["slug"][c]), g["modified"], "0.8" if c == "es" else "0.7", alts))
    return "\n\n".join(entries)


def write_sitemap(guides):
    text = SITEMAP.read_text(encoding="utf-8")
    pattern = re.compile(r"(<!-- guides:start[^>]*-->).*?(\n[ \t]*<!-- guides:end -->)", re.S)
    if not pattern.search(text):
        fail("faltan los marcadores guides:start / guides:end en sitemap.xml")
    SITEMAP.write_text(pattern.sub(lambda m: m.group(1) + "\n\n" + sitemap_block(guides) + "\n" + m.group(2),
                                   text, 1), encoding="utf-8")


def landing_strings(code):
    """Textos de las tarjetas de guía de la landing: (español -> idioma), para build.py."""
    _, guides = load()
    out = {}
    for g in guides.values():
        out[g["es"]["card_title"]] = g[code]["card_title"]
        out[g["es"]["card_desc"]] = g[code]["card_desc"]
    return out


def landing_hrefs(code):
    """Enlaces de la landing española a cada guía -> el mismo enlace en la landing de `code`."""
    _, guides = load()
    return {'href="%s"' % guide_path("es", g["slug"]["es"]):
            'href="%s"' % rel(landing_path(code), guide_path(code, g["slug"][code]))
            for g in guides.values()}


# ---------------------------------------------------------------- principal

def landing_file(code):
    return ROOT / "index.html" if code == "es" else ROOT / code / "index.html"


def main():
    ui, guides = load()
    validate(ui, guides)

    chromes = {}
    for code in LANGS:
        f = landing_file(code)
        if not f.exists():
            fail("falta %s: ejecuta primero python web/_i18n/build.py" % f)
        chromes[code] = cut_chrome(f.read_text(encoding="utf-8"))

    pages = []
    for gid, g in guides.items():
        for code in LANGS:
            pages.append((gid, code) + build_page(gid, g, code, ui, guides, chromes[code]))

    # Comprobación final de cada página antes de escribir ninguna.
    for gid, code, path, page in pages:
        for needle in ('<link rel="canonical"', 'hreflang="x-default"', 'data-goatcounter-click="guia-%s-cta"' % gid,
                       'data-goatcounter-click="guia-%s-descarga-win"' % gid, 'application/ld+json',
                       '<meta name="description"'):
            if needle not in page:
                fail("[%s/%s] la página generada no contiene %s" % (gid, code, needle))
        for i, block in enumerate(re.findall(r'<script type="application/ld\+json">(.*?)</script>', page, re.S), 1):
            json.loads(block)
        if page.count("<h1") != 1:
            fail("[%s/%s] debe haber exactamente un h1" % (gid, code))
        if re.search(r"\{(home|guide:[a-z0-9-]+)\}", page):
            fail("[%s/%s] queda un marcador sin resolver" % (gid, code))

    for gid, code, path, page in pages:
        target = ROOT / path / "index.html"
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(page, encoding="utf-8")
        print("guía %-7s %s -> %sindex.html" % (gid, code, path))
    write_sitemap(guides)
    print("sitemap.xml actualizado con %d páginas de guías" % len(pages))


if __name__ == "__main__":
    main()
