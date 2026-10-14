#!/usr/bin/env python3
"""
Genera web/assets/mc/ a partir del .jar de Minecraft instalado: las texturas del paisaje,
los iconos de las tarjetas, los sprites de la interfaz y la letra del juego como fuente web.

    python web/_tools/mc_assets.py "%APPDATA%/.minecraft/versions/26.2/26.2.jar"

Necesita Pillow y fontTools. Las texturas son de Mojang: se sacan del juego en vez de
dibujarlas a mano para que sean idénticas, y solo se copian las que usa la web.

Qué hace con cada cosa:
- Las texturas que el juego tiñe según el bioma (hierba, hojas, agua) vienen en
  gris; aquí se tiñen con los colores de las llanuras, como se ven en una partida normal.
- Las animadas (el agua) se dejan como tira vertical de fotogramas; la web la recorre.
- Los bloques que hacen de icono (mesa de crafteo, cofre...) se renderizan en isométrico
  con la misma proyección que el inventario: giro de 225° y 30°, caras laterales al 80 %
  y 60 % de luz.
- La letra se convierte de mapa de bits a contornos: cada píxel encendido es un cuadrado.
"""
import io
import json
import math
import os
import pathlib
import sys
import zipfile

from PIL import Image, ImageDraw
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools import subset

OUT = pathlib.Path(__file__).resolve().parent.parent / "assets" / "mc"
TEX = "assets/minecraft/textures/"

GRASS = (0x91, 0xBD, 0x59)     # colores de las llanuras
FOLIAGE = (0x77, 0xAB, 0x2F)
WATER = (0x3F, 0x76, 0xE4)


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    jar = zipfile.ZipFile(sys.argv[1])
    OUT.mkdir(parents=True, exist_ok=True)

    def load(path):
        return Image.open(io.BytesIO(jar.read(TEX + path + ".png"))).convert("RGBA")

    def save(im, name):
        im.save(OUT / (name + ".png"), optimize=True)

    def tint(im, rgb):
        out = im.copy()
        px = out.load()
        for y in range(out.height):
            for x in range(out.width):
                r, g, b, a = px[x, y]
                px[x, y] = (r * rgb[0] // 255, g * rgb[1] // 255, b * rgb[2] // 255, a)
        return out

    # ---------- paisaje ----------
    side = load("block/grass_block_side")
    side.alpha_composite(tint(load("block/grass_block_side_overlay"), GRASS))
    save(side, "grass_side")
    for name in ("dirt", "stone", "sand", "gravel", "oak_log", "poppy", "dandelion", "cornflower",
                 "oxeye_daisy", "azure_bluet"):
        save(load("block/" + name), name)
    save(tint(load("block/oak_leaves"), FOLIAGE), "oak_leaves")
    for name in ("short_grass", "fern", "tall_grass_bottom", "tall_grass_top", "sugar_cane"):
        save(tint(load("block/" + name), GRASS), name)
    save(tint(load("block/water_still"), WATER), "water")          # 16x512: 32 fotogramas
    save(load("environment/celestial/sun"), "sun")
    save(load("environment/celestial/moon/full_moon"), "moon")
    save(load("environment/clouds"), "clouds")
    # Partículas de hoja que caen de los robles: 12 variantes en una tira horizontal.
    leaves = [tint(load("particle/leaf_%d" % i), FOLIAGE) for i in range(12)]
    strip = Image.new("RGBA", (sum(l.width for l in leaves), max(l.height for l in leaves)))
    x = 0
    for l in leaves:
        strip.paste(l, (x, 0))
        x += l.width
    save(strip, "leaf_particles")

    # ---------- interfaz ----------
    for path, name in (("gui/sprites/widget/button", "button"),
                       ("gui/sprites/widget/button_highlighted", "button_hover"),
                       ("gui/sprites/container/slot", "slot"),
                       ("gui/sprites/hud/experience_bar_background", "xp_bg"),
                       ("gui/sprites/hud/experience_bar_progress", "xp_fg")):
        save(load(path), name)
    # Botón verde para la descarga: el mismo sprite con la luz del original y tono verde.
    for src, name in (("button", "button_green"), ("button_hover", "button_green_hover")):
        im = Image.open(OUT / (src + ".png")).convert("RGBA")
        px = im.load()
        for y in range(im.height):
            for x in range(im.width):
                r, g, b, a = px[x, y]
                l = (r + g + b) / 3 / 255
                px[x, y] = (min(255, int(96 * l)), min(255, int(212 * l)), min(255, int(56 * l)), a) if a else (0, 0, 0, 0)
        save(im, name)

    # ---------- iconos ----------
    for path, name in (("item/enchanted_book", "enchanted_book"), ("item/ender_pearl", "ender_pearl"),
                       ("item/totem_of_undying", "totem"), ("item/diamond_pickaxe", "diamond_pickaxe")):
        save(load(path), name)

    first = lambda im: im.crop((0, 0, im.width, im.width))
    save(iso(load("block/crafting_table_top"), load("block/crafting_table_front"),
             load("block/crafting_table_side")), "iso_crafting")
    cb = lambda n: first(load("block/command_block_" + n))
    save(iso(cb("side"), cb("front"), cb("side")), "iso_command")
    save(iso(load("block/barrel_top"), load("block/barrel_side"), load("block/barrel_side")), "iso_barrel")
    save(iso(tint(load("block/grass_block_top"), GRASS), side, side), "iso_grass")
    save(chest(load("entity/chest/normal")), "iso_chest")

    # ---------- letra ----------
    build_font(jar)
    print("Listo:", len(list(OUT.iterdir())), "archivos en", OUT)


# ---------------------------------------------------------------- isométrico
S = 8   # píxeles de salida por texel


def proj(x, y, z):
    c = math.cos(math.radians(45))
    return ((x - z) * c, (x + z) * c * math.sin(math.radians(30)) - y * math.cos(math.radians(30)))


def face(draw, tex, origin, u, v, shade, off):
    """Cada texel es un paralelogramo origin + i*u + j*v."""
    for j in range(tex.height):
        for i in range(tex.width):
            r, g, b, a = tex.getpixel((i, j))
            if a < 10:
                continue
            pts = []
            for di, dj in ((0, 0), (1, 0), (1, 1), (0, 1)):
                p = [origin[k] + (i + di) * u[k] + (j + dj) * v[k] for k in range(3)]
                px, py = proj(*p)
                pts.append(((px - off[0]) * S, (py - off[1]) * S))
            draw.polygon(pts, fill=(int(r * shade), int(g * shade), int(b * shade), 255))


def canvas_for(w, h, d):
    corners = [proj(x, y, z) for x in (0, w) for y in (0, h) for z in (0, d)]
    minx = min(c[0] for c in corners)
    miny = min(c[1] for c in corners)
    W = int(math.ceil((max(c[0] for c in corners) - minx) * S)) + 2
    H = int(math.ceil((max(c[1] for c in corners) - miny) * S)) + 2
    im = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    return im, ImageDraw.Draw(im), (minx, miny)


def fit(im, size=128):
    out = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    k = min(size / im.width, size / im.height)
    r = im.resize((round(im.width * k), round(im.height * k)), Image.LANCZOS)
    out.alpha_composite(r, ((size - r.width) // 2, (size - r.height) // 2))
    return out


def iso(top, left, right):
    im, dr, off = canvas_for(16, 16, 16)
    face(dr, left, (0, 16, 16), (1, 0, 0), (0, -1, 0), 0.8, off)
    face(dr, right, (16, 16, 16), (0, 0, -1), (0, -1, 0), 0.6, off)
    face(dr, top, (0, 16, 0), (1, 0, 0), (0, 0, 1), 1.0, off)
    return fit(im)


def chest(tex):
    """El cofre es una entidad: su textura de 64x64 trae caja (alto 10) y tapa (alto 5)."""
    crop = lambda x, y, w, h: tex.crop((x, y, x + w, y + h))
    im, dr, off = canvas_for(14, 15, 14)
    face(dr, crop(42, 33, 14, 10), (0, 10, 14), (1, 0, 0), (0, -1, 0), 0.8, off)
    face(dr, crop(28, 33, 14, 10), (14, 10, 14), (0, 0, -1), (0, -1, 0), 0.6, off)
    face(dr, crop(42, 14, 14, 5), (0, 15, 14), (1, 0, 0), (0, -1, 0), 0.8, off)
    face(dr, crop(28, 14, 14, 5), (14, 15, 14), (0, 0, -1), (0, -1, 0), 0.6, off)
    face(dr, crop(28, 0, 14, 14).transpose(Image.FLIP_TOP_BOTTOM), (0, 15, 0), (1, 0, 0), (0, 0, 1), 1.0, off)
    face(dr, crop(1, 1, 2, 4), (6, 11, 15), (1, 0, 0), (0, -1, 0), 0.8, off)
    return fit(im)


# ---------------------------------------------------------------- letra
P = 128          # unidades por píxel del juego; el em mide 8 píxeles


def build_font(jar):
    """Letra del juego en WOFF. A 16 px cada píxel del juego ocupa 2 px de pantalla.

    Se queda solo con el alfabeto latino (los cinco idiomas de la web) para que pese poco.
    """
    providers = json.loads(jar.read("assets/minecraft/font/include/default.json"))["providers"]
    space = json.loads(jar.read("assets/minecraft/font/include/space.json"))["providers"][0]["advances"]
    glyphs = {}
    for prov in providers:           # en el juego gana el primer proveedor que tiene el carácter
        if prov["type"] != "bitmap":
            continue
        im = Image.open(io.BytesIO(jar.read(TEX + prov["file"].split(":")[1]))).convert("RGBA")
        rows = prov["chars"]
        cw, ch = im.width // len(rows[0]), im.height // len(rows)
        scale = prov.get("height", 8) / ch
        for r, line in enumerate(rows):
            for c, char in enumerate(line):
                cp = ord(char)
                if cp == 0 or cp in glyphs:
                    continue
                px = im.crop((c * cw, r * ch, (c + 1) * cw, (r + 1) * ch)).load()
                on = [(x, y) for y in range(ch) for x in range(cw) if px[x, y][3] > 127]
                if on:
                    glyphs[cp] = (on, max(x for x, _ in on) + 1, scale, prov["ascent"])

    order, cmap = [".notdef", "space"], {32: "space"}
    pen = TTGlyphPen(None)
    for x0, x1 in ((0, 5),):
        pen.moveTo((x0 * P, 0)); pen.lineTo((x0 * P, 7 * P)); pen.lineTo((x1 * P, 7 * P)); pen.lineTo((x1 * P, 0)); pen.closePath()
    outlines = {".notdef": pen.glyph(), "space": TTGlyphPen(None).glyph()}
    advance = {".notdef": 6 * P, "space": int(space.get(" ", 4)) * P}
    for cp, (on, width, scale, ascent) in sorted(glyphs.items()):
        if cp == 32:
            continue
        name = "u%04X" % cp
        order.append(name)
        cmap[cp] = name
        pen = TTGlyphPen(None)
        s = P * scale
        rows = {}
        for x, y in on:
            rows.setdefault(y, []).append(x)
        for y, xs in rows.items():
            xs.sort()
            runs, start = [], xs[0]
            for a, b in zip(xs, xs[1:] + [None]):
                if b != a + 1:
                    runs.append((start, a))
                    start = b
            top = (ascent - y) * s
            for a, b in runs:
                pen.moveTo((a * s, top - s)); pen.lineTo((a * s, top)); pen.lineTo(((b + 1) * s, top)); pen.lineTo(((b + 1) * s, top - s)); pen.closePath()
        outlines[name] = pen.glyph()
        advance[name] = int(round((width * scale + 1) * P))

    fb = FontBuilder(8 * P, isTTF=True)
    fb.setupGlyphOrder(order)
    fb.setupCharacterMap(cmap)
    fb.setupGlyf(outlines)
    fb.setupHorizontalMetrics({n: (advance[n], 0) for n in order})
    fb.setupHorizontalHeader(ascent=9 * P, descent=-3 * P)
    fb.setupNameTable({"familyName": "Minecraft Web", "styleName": "Regular"})
    fb.setupOS2(sTypoAscender=9 * P, sTypoDescender=-3 * P, sTypoLineGap=0,
                usWinAscent=10 * P, usWinDescent=3 * P, fsSelection=0x40)
    fb.setupPost()

    opts = subset.Options()
    opts.flavor = "woff"
    sub = subset.Subsetter(opts)
    sub.populate(unicodes=list(range(0x20, 0x250)) + list(range(0x2010, 0x2040)) + [0x20AC, 0x2122, 0x2192])
    sub.subset(fb.font)
    fb.font.flavor = "woff"
    fb.font.save(OUT / "minecraft.woff")


if __name__ == "__main__":
    main()
