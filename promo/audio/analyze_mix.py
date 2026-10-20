"""Draws a spectrogram and prints loudness per section, to check a mix without listening to it."""
import json, sys, wave
from pathlib import Path
import numpy as np
from scipy import signal
from PIL import Image, ImageDraw
ROOT = Path(__file__).resolve().parent.parent
name = sys.argv[1]
w = wave.open(str(ROOT / "public" / "audio" / f"music-{name}.wav"))
x = np.frombuffer(w.readframes(w.getnframes()), "<i2").reshape(-1, 2).astype(float) / 32768
sr = w.getframerate(); m = x.mean(1)
print("peak", np.abs(x).max().round(3), "rms dBFS", (20*np.log10(np.sqrt((m**2).mean()))).round(1))
E = json.loads((ROOT / "src" / "edits.json").read_text()); beat = 60 / E["bpm"]; t = 0
for s, b in E["edits"][name]["sections"]:
    seg = m[int(t*sr):int((t+b*beat)*sr)]
    print(f"{s:12s} {t:5.1f}s  rms {20*np.log10(np.sqrt((seg**2).mean())+1e-9):6.1f} dB")
    t += b*beat
f, tt, S = signal.stft(m, sr, nperseg=2048, noverlap=1536)
S = 20*np.log10(np.abs(S)+1e-7); S = S[(f<12000)][::-1]
img = np.clip((S+90)/70, 0, 1)
im = Image.fromarray((img*255).astype(np.uint8)).resize((1600, 500))
d = ImageDraw.Draw(im); t = 0
for s, b in E["edits"][name]["sections"]:
    x0 = int(t/ (len(m)/sr) * 1600); d.line([(x0,0),(x0,500)], fill=255); d.text((x0+3,3), s, fill=255); t += b*beat
im.save(ROOT / "public" / "audio" / f"_spec-{name}.png")
