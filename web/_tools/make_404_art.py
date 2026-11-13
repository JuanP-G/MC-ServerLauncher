"""Genera web/assets/404/code.webp: el «404» de la página de error, hecho con cubos de piedra.

Cada cifra es una rejilla de 6x9 cubos; cada cubo es la textura de piedra del juego
(web/_tools/mc-src/stone.png) con un bisel de luz arriba-izquierda y sombra abajo-derecha, una
extrusión oscura detrás, una sombra suave y un resplandor morado (el del portal). Se guarda en
WebP sin pérdida: son bordes duros y se verían sucios con pérdida.

La ilustración de fondo (web/assets/404/hero.webp) NO sale de aquí: es una imagen generada con IA
a partir del aspecto de Minecraft, y se guarda tal cual.

Uso (necesita Pillow):  python web/_tools/make_404_art.py
"""
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter

WEB = Path(__file__).resolve().parent.parent
STONE = WEB / "_tools" / "mc-src" / "stone.png"
OUT = WEB / "assets" / "404" / "code.webp"

GLYPH = {
    "4": ["...XX.", "..XXX.", ".XX.XX", "XX..XX", "XXXXXX", "XXXXXX", "....XX", "....XX", "....XX"],
    "0": [".XXXX.", "XXXXXX", "XX..XX", "XX..XX", "XX..XX", "XX..XX", "XX..XX", "XXXXXX", ".XXXX."],
}

CELL = 40       # píxeles de la imagen por cubo
PAD = 36        # margen para que quepan la sombra y el resplandor
DEPTH = 14      # grosor de la extrusión, en píxeles
GAP_COLS = 1    # columnas vacías entre cifras


def cube(tex):
    t = tex.resize((CELL, CELL), Image.NEAREST)
    r, g, b, a = t.split()
    t = Image.merge("RGBA", (
        r.point(lambda v: min(255, int(v * 1.15))),
        g.point(lambda v: min(255, int(v * 1.15))),
        b.point(lambda v: min(255, int(v * 1.20))),
        a,
    ))
    d = ImageDraw.Draw(t)
    for i in range(3):
        d.line([(i, i), (CELL - 1 - i, i)], fill=(255, 255, 255, 70 - i * 18))
        d.line([(i, i), (i, CELL - 1 - i)], fill=(255, 255, 255, 50 - i * 14))
        d.line([(i, CELL - 1 - i), (CELL - 1 - i, CELL - 1 - i)], fill=(0, 0, 0, 120 - i * 25))
        d.line([(CELL - 1 - i, i), (CELL - 1 - i, CELL - 1 - i)], fill=(0, 0, 0, 100 - i * 25))
    return t


def lettering(text="404"):
    rows = max(len(GLYPH[c]) for c in text)
    cols = sum(len(GLYPH[c][0]) for c in text) + GAP_COLS * (len(text) - 1)
    w = cols * CELL + PAD * 2 + DEPTH
    h = rows * CELL + PAD * 2 + DEPTH

    c = cube(Image.open(STONE).convert("RGBA"))
    base = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    mask = Image.new("L", (w, h), 0)
    md = ImageDraw.Draw(mask)
    x0 = PAD
    for ch in text:
        g = GLYPH[ch]
        for ry, line in enumerate(g):
            for rx, v in enumerate(line):
                if v == "X":
                    px, py = x0 + rx * CELL, PAD + ry * CELL
                    base.alpha_composite(c, (px, py))
                    md.rectangle([px, py, px + CELL - 1, py + CELL - 1], fill=255)
        x0 += (len(g[0]) + GAP_COLS) * CELL

    ext = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    dark = Image.new("RGBA", (w, h), (14, 12, 34, 255))
    for i in range(DEPTH, 0, -1):
        ext.paste(dark, (0, 0), ImageChops.offset(mask, i, i))

    shadow = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    shadow.paste(Image.new("RGBA", (w, h), (0, 0, 0, 170)), (0, 0),
                 ImageChops.offset(mask.filter(ImageFilter.GaussianBlur(14)), 10, 18))

    glow = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    glow.paste(Image.new("RGBA", (w, h), (150, 80, 255, 120)), (0, 0), mask.filter(ImageFilter.GaussianBlur(26)))

    out = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    for layer in (glow, shadow, ext, base):
        out.alpha_composite(layer)
    return out


if __name__ == "__main__":
    OUT.parent.mkdir(parents=True, exist_ok=True)
    img = lettering()
    img.save(OUT, "WEBP", lossless=True, quality=100, method=6)
    print(OUT, img.size, OUT.stat().st_size, "bytes")
