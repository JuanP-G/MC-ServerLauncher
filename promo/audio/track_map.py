"""Maps a song's structure without listening to it: loudness per second, the tempo and where the
beats fall, and a spectrogram with a time scale. Used to choose where to cut a long track down.

    python audio/track_map.py public/music/sweden.wav
"""
import sys
import wave
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw
from scipy import signal

path = Path(sys.argv[1])
w = wave.open(str(path))
sr = w.getframerate()
x = np.frombuffer(w.readframes(w.getnframes()), "<i2").reshape(-1, 2).astype(float).mean(1) / 32768
dur = len(x) / sr

# Loudness per second, as a bar chart in text.
print(f"{path.name}: {dur:.1f}s, peak {np.abs(x).max():.2f}")
rms = [20 * np.log10(np.sqrt(np.mean(x[int(s * sr):int((s + 1) * sr)] ** 2)) + 1e-9) for s in range(int(dur))]
for s in range(0, len(rms), 2):
    db = max(rms[s], -60)
    print(f"{s // 60}:{s % 60:02d} {db:6.1f} {'#' * int((db + 60) / 1.5)}")

# Onset strength (spectral flux) and its autocorrelation for the tempo.
hop = 512
f, t, S = signal.stft(x, sr, nperseg=2048, noverlap=2048 - hop)
mag = np.abs(S)
flux = np.maximum(0, np.diff(np.log1p(mag * 50), axis=1)).sum(0)
flux = (flux - flux.mean()) / (flux.std() + 1e-9)
fps = sr / hop
ac = np.correlate(flux, flux, "full")[len(flux) - 1:]
lags = np.arange(len(ac)) / fps
ok = (lags > 60 / 180) & (lags < 60 / 60)
best = lags[ok][np.argmax(ac[ok])]
print(f"tempo ~ {60 / best:.1f} BPM (beat {best:.3f}s)")
# Phase: the offset that puts most onset strength on the beat grid.
n_beats = int(dur / best)
phases = np.linspace(0, best, 40, endpoint=False)
score = [sum(flux[min(len(flux) - 1, int((p + k * best) * fps))] for k in range(n_beats)) for p in phases]
phase = phases[int(np.argmax(score))]
print(f"first beat ~ {phase:.3f}s")

# Spectrogram, one pixel column per 0.25 s, with a tick every 10 s.
img = 20 * np.log10(mag[(f < 8000)][::-1] + 1e-7)
img = np.clip((img + 80) / 70, 0, 1)
cols = int(dur * 4)
im = Image.fromarray((img * 255).astype(np.uint8)).resize((cols, 300))
d = ImageDraw.Draw(im)
for s in range(0, int(dur), 10):
    xpix = int(s * 4)
    d.line([(xpix, 0), (xpix, 300)], fill=255 if s % 60 == 0 else 120)
    d.text((xpix + 2, 2), f"{s // 60}:{s % 60:02d}", fill=255)
out = path.with_name(f"_map-{path.stem}.png")
im.save(out)
print(out)
