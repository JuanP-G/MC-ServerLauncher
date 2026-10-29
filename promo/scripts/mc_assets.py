"""Takes what the brand videos need from your own Minecraft install, so nothing is downloaded.

- Block textures from the game's jar, into public/mc/.
- The nine default player skins, each put together as a front view (16 x 32 px), into
  public/mc/players/.
- C418's music from the game's assets, decoded to WAV into public/music/ (not kept in git).
  Those tracks are on Mojang's "Minecraft Creator-Safe" list; credit them as
  "Music courtesy of Mojang Studios".

    python scripts/mc_assets.py            # uses %APPDATA%/.minecraft and version 26.2
    python scripts/mc_assets.py 26.3
"""
import glob
import json
import os
import pathlib
import subprocess
import sys
import zipfile
from io import BytesIO

from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parent.parent
MC = pathlib.Path(os.environ.get("APPDATA", "")) / ".minecraft"
VERSION = sys.argv[1] if len(sys.argv) > 1 else "26.2"

BLOCKS = [
    "oak_planks", "spruce_planks", "dark_oak_planks", "cobblestone", "mossy_cobblestone", "stone_bricks",
    "bricks", "glass", "torch", "oak_door_top", "oak_door_bottom", "spruce_log", "glowstone",
    "white_wool", "red_wool", "hay_block_side", "bookshelf",
]
MUSIC = {"aria_math": "creative/aria_math", "sweden": "sweden"}
FFMPEG = ROOT / "node_modules" / "@remotion" / "compositor-win32-x64-msvc" / "ffmpeg.exe"


def blocks():
    jar = zipfile.ZipFile(MC / "versions" / VERSION / f"{VERSION}.jar")
    out = ROOT / "public" / "mc"
    for name in BLOCKS:
        (out / f"{name}.png").write_bytes(jar.read(f"assets/minecraft/textures/block/{name}.png"))
    print(f"{len(BLOCKS)} block textures from {VERSION}")


SKINS = ["steve", "alex", "kai", "sunny", "zuri", "ari", "noor", "efe", "makena"]
# (source box in the 64 x 64 skin, where it goes in the 16 x 32 front view): base layer, then overlays.
FRONT = [
    ((8, 8, 16, 16), (4, 0)), ((20, 20, 28, 32), (4, 8)), ((44, 20, 48, 32), (0, 8)), ((36, 52, 40, 64), (12, 8)),
    ((4, 20, 8, 32), (4, 20)), ((20, 52, 24, 64), (8, 20)),
    ((40, 8, 48, 16), (4, 0)), ((20, 36, 28, 48), (4, 8)), ((44, 36, 48, 48), (0, 8)), ((52, 52, 56, 64), (12, 8)),
    ((4, 36, 8, 48), (4, 20)), ((4, 52, 8, 64), (8, 20)),
]


def players():
    jar = zipfile.ZipFile(MC / "versions" / VERSION / f"{VERSION}.jar")
    out = ROOT / "public" / "mc" / "players"
    out.mkdir(parents=True, exist_ok=True)
    for name in SKINS:
        skin = Image.open(BytesIO(jar.read(f"assets/minecraft/textures/entity/player/wide/{name}.png"))).convert("RGBA")
        front = Image.new("RGBA", (16, 32))
        for box, at in FRONT:
            part = skin.crop(box)
            front.alpha_composite(part, at)
        front.save(out / f"{name}.png")
    print(f"{len(SKINS)} players")


def music():
    index = max(glob.glob(str(MC / "assets" / "indexes" / "*.json")), key=os.path.getmtime)
    objects = json.loads(pathlib.Path(index).read_text())["objects"]
    out = ROOT / "public" / "music"
    out.mkdir(parents=True, exist_ok=True)
    for name, path in MUSIC.items():
        h = objects[f"minecraft/sounds/music/game/{path}.ogg"]["hash"]
        src = MC / "assets" / "objects" / h[:2] / h
        subprocess.run([str(FFMPEG), "-y", "-loglevel", "error", "-i", str(src), "-ar", "48000", "-ac", "2",
                        str(out / f"{name}.wav")], check=True)
        print(f"{name}.wav")


if __name__ == "__main__":
    blocks()
    players()
    music()
