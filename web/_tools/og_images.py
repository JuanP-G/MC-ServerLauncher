#!/usr/bin/env python3
"""
Genera las imágenes para compartir el enlace (Open Graph / Twitter), una por idioma:
web/assets/og/es.jpg, en.jpg, de.jpg, fr.jpg y pt.jpg, de 1200x630.

    python web/_tools/og_images.py [ruta a chrome.exe o msedge.exe]

Las dibuja Chrome (o Edge) sin ventana a partir de una plantilla HTML con los recursos de
la propia web, así salen con la misma letra, texturas y captura. Los textos se sacan de
web/_i18n/strings.json, de modo que si cambia el titular de la web basta con volver a
ejecutar el script después de build.py.

1200x630 es el tamaño que piden Facebook, LinkedIn, X, Discord, WhatsApp y Telegram para
la tarjeta grande; con la captura sola (16:9) varias de ellas recortaban los lados.
"""
import html
import json
import pathlib
import shutil
import subprocess
import sys
import tempfile

from PIL import Image

WEB = pathlib.Path(__file__).resolve().parent.parent
OUT = WEB / "assets" / "og"
STRINGS = WEB / "_i18n" / "strings.json"

BROWSERS = [
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    "/usr/bin/google-chrome", "/usr/bin/chromium",
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
]

SPANISH = {
    "pre": "Crea tu servidor de ", "post": " en tu propio PC.",
    "free": "Gratis y de código abierto", "os": "Windows · Linux · macOS",
}

TEMPLATE = """<!doctype html><html lang="{lang}"><head><meta charset="utf-8"><style>
@font-face{{font-family:MC; src:url("{assets}/mc/minecraft.woff") format("woff")}}
@font-face{{font-family:Inter; src:local("Inter"), local("Segoe UI")}}
html,body{{margin:0; width:1200px; height:630px; overflow:hidden}}
body{{position:relative; background:linear-gradient(180deg,#0D2A4C 0%,#17416E 55%,#2B6496 100%);
  font-family:"Segoe UI",Inter,system-ui,sans-serif; color:#fff}}
.sun{{position:absolute; left:500px; top:-40px; width:192px; height:192px; image-rendering:pixelated; mix-blend-mode:screen}}
.cloud{{position:absolute; height:22px; background:#fff; box-shadow:0 10px 0 #B9C4D2}}
.ground{{position:absolute; left:0; right:0; bottom:0; height:96px; image-rendering:pixelated;
  background:url("{assets}/mc/grass_side.png") 0 0/48px 48px repeat-x, url("{assets}/mc/dirt.png") 0 48px/48px 48px repeat-x}}
.text{{position:absolute; left:64px; top:70px; width:560px}}
.logo{{width:96px; height:96px; image-rendering:pixelated; display:block; margin-bottom:26px;
  filter:drop-shadow(0 6px 0 rgba(0,0,0,.35))}}
h1{{margin:0 0 22px; font:400 48px/1.22 MC,monospace; -webkit-font-smoothing:none; text-shadow:6px 6px 0 #3F3F3F}}
h1 em{{font-style:normal; color:#80FF20; text-shadow:6px 6px 0 #204008}}
p{{margin:0; font-size:24px; font-weight:600; color:#E2ECF6; text-shadow:0 2px 4px rgba(0,0,0,.6)}}
p.os{{margin-top:6px; color:#80FF20; font:400 24px MC,monospace; -webkit-font-smoothing:none; text-shadow:3px 3px 0 #204008}}
.url{{position:absolute; left:64px; bottom:28px; font:400 24px MC,monospace; -webkit-font-smoothing:none;
  color:#fff; text-shadow:3px 3px 0 #3F3F3F}}
.shot{{position:absolute; left:660px; top:120px; width:600px; border-radius:12px; overflow:hidden;
  border:1px solid #1E2D45; background:#0D1524; box-shadow:0 30px 70px rgba(0,0,0,.55);
  transform:perspective(1400px) rotateY(-9deg) rotateX(3deg)}}
.bar{{display:flex; align-items:center; gap:9px; padding:10px 14px; background:#111C2E; border-bottom:1px solid #1E2D45;
  font-size:13px; font-weight:600}}
.bar img{{width:18px; height:18px; image-rendering:pixelated}}
.bar i{{margin-left:auto; color:#93A4BC; font-style:normal; font-size:12px; letter-spacing:2px}}
.shot > img{{display:block; width:100%}}
</style></head><body>
<img class="sun" src="{assets}/mc/sun.png" alt="">
<div class="cloud" style="left:90px; top:38px; width:240px"></div>
<div class="cloud" style="left:300px; top:70px; width:150px"></div>
<div class="cloud" style="left:760px; top:44px; width:300px"></div>
<div class="cloud" style="left:1020px; top:82px; width:140px"></div>
<div class="ground"></div>
<div class="text">
  <img class="logo" src="{assets}/logo.png" alt="">
  <h1>{pre}<em>Minecraft</em>{post}</h1>
  <p>{free}</p>
  <p class="os">{os}</p>
</div>
<div class="shot"><div class="bar"><img src="{assets}/logo.png" alt=""><b>MC Server Launcher</b><i>—  ▢  ✕</i></div>
  <img src="{assets}/app-screenshot.jpg" alt=""></div>
<div class="url">mc-server-launcher.vercel.app</div>
</body></html>"""


def find_browser():
    if len(sys.argv) > 1:
        return sys.argv[1]
    for b in BROWSERS:
        if pathlib.Path(b).exists():
            return b
    return shutil.which("chrome") or shutil.which("chromium") or sys.exit("No encuentro Chrome ni Edge: pasa la ruta.")


def main():
    browser = find_browser()
    data = json.loads(STRINGS.read_text(encoding="utf-8"))
    langs = {"es": SPANISH}
    for code in (k for k in data if not k.startswith("_")):
        s = data[code]["strings"]
        langs[code] = {k: s.get(v, v) for k, v in SPANISH.items()}
    OUT.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        for code, t in langs.items():
            page = pathlib.Path(tmp) / (code + ".html")
            page.write_text(TEMPLATE.format(lang=code, assets=(WEB / "assets").as_uri(),
                                            **{k: html.escape(v) for k, v in t.items()}), encoding="utf-8")
            png = pathlib.Path(tmp) / (code + ".png")
            subprocess.run([browser, "--headless=new", "--disable-gpu", "--hide-scrollbars",
                            "--force-device-scale-factor=1", "--allow-file-access-from-files",
                            "--virtual-time-budget=3000", "--window-size=1200,630",
                            "--screenshot=" + str(png), page.as_uri()],
                           check=True, capture_output=True, timeout=60)
            Image.open(png).convert("RGB").crop((0, 0, 1200, 630)).save(
                OUT / (code + ".jpg"), quality=86, optimize=True, progressive=True)
            print(code, "->", (OUT / (code + ".jpg")).relative_to(WEB))


if __name__ == "__main__":
    main()
