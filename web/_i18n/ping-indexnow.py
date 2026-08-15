#!/usr/bin/env python3
"""
Avisa a los buscadores que admiten IndexNow (Bing, DuckDuckGo, Yandex, Naver, Seznam)
de que las páginas han cambiado, en lugar de esperar a que pasen a rastrear por su
cuenta. Google NO participa en IndexNow: allí el aviso lo da el sitemap.

    python web/_i18n/ping-indexnow.py

La clave vive en un archivo de texto en la raíz del sitio, con el mismo nombre que su
contenido: así el buscador comprueba que quien avisa controla el dominio. Si se borra
ese archivo, los avisos dejan de aceptarse.
"""

import json
import sys
import urllib.request

KEY = "7f01ac7bcf15afc35e062dd84ebe02d1"
HOST = "mc-server-launcher.vercel.app"
BASE = "https://" + HOST

URLS = [BASE + p for p in ("/", "/en/", "/de/", "/fr/", "/pt/")]

payload = {
    "host": HOST,
    "key": KEY,
    "keyLocation": "%s/%s.txt" % (BASE, KEY),
    "urlList": URLS,
}

req = urllib.request.Request(
    "https://api.indexnow.org/indexnow",
    data=json.dumps(payload).encode("utf-8"),
    headers={"Content-Type": "application/json; charset=utf-8"},
    method="POST",
)

try:
    with urllib.request.urlopen(req, timeout=30) as resp:
        code = resp.status
        body = resp.read().decode("utf-8", "replace").strip()
except urllib.error.HTTPError as err:
    code = err.code
    body = err.read().decode("utf-8", "replace").strip()

# 200 = aceptado. 202 = aceptado, pendiente de validar la clave (normal la primera vez).
print("HTTP %s %s" % (code, body or ""))
for u in URLS:
    print("  enviada:", u)

if code not in (200, 202):
    sys.exit("IndexNow ha rechazado el aviso.")
