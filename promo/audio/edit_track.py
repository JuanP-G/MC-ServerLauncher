"""Cuts a long track down to the length of an edit without listening to it.

Each edit is a list of rough (start, end) stretches of the song in seconds. Every boundary is moved
onto the song's beat grid, and each join is searched a few beats either way for the pair of points
whose harmony (chroma) and loudness match best, then crossfaded over one beat with equal power.
The result is levelled and written to public/music/, with where each stretch lands in
src/music-edits.json, so the video can put its moments on the music's.

    python audio/edit_track.py
"""
import json
import wave
from pathlib import Path

import numpy as np
from scipy import signal

ROOT = Path(__file__).resolve().parent.parent
SR = 48000

# The rough stretches of each song, in seconds of the source. Tempo and first beat come from
# audio/track_map.py.
EDITS = {
    "c45": {"song": "aria_math", "bpm": 84.0, "phase": 0.304, "target": 45.0, "fades": [2, 1], "parts": [(0.0, 10.0), (80.0, 96.0), (286.0, 306.0)]},
    "c30": {"song": "aria_math", "bpm": 84.0, "phase": 0.304, "target": 30.0, "fades": [2, 1], "parts": [(0.0, 6.0), (80.0, 91.5), (292.0, 306.0)]},
    "b45": {"song": "sweden", "bpm": 87.9, "phase": 0.614, "target": 45.0, "fades": [1], "parts": [(46.0, 74.0), (104.0, 121.5)]},
    "b30": {"song": "sweden", "bpm": 87.9, "phase": 0.614, "target": 30.0, "fades": [1], "parts": [(58.0, 74.0), (106.0, 121.5)]},
}
SEARCH_BEATS = 2


def load(name):
    w = wave.open(str(ROOT / "public" / "music" / f"{name}.wav"))
    return np.frombuffer(w.readframes(w.getnframes()), "<i2").reshape(-1, 2).astype(float) / 32768


def chroma(x, t, length=1.5):
    """Twelve pitch classes of the stretch ending at t (seconds), from its spectrum."""
    a = max(0, int((t - length) * SR))
    seg = x[a:int(t * SR)].mean(1)
    if len(seg) < 2048:
        return np.zeros(13)
    f, p = signal.welch(seg, SR, nperseg=4096)
    keep = (f > 55) & (f < 4000)
    pc = np.round(12 * np.log2(f[keep] / 440.0)).astype(int) % 12
    c = np.bincount(pc, weights=p[keep], minlength=12)
    c = c / (np.linalg.norm(c) + 1e-12)
    loud = 10 * np.log10(np.mean(seg ** 2) + 1e-12)
    return np.append(c, loud)


def similarity(a, b):
    return float(np.dot(a[:12], b[:12])) - abs(a[12] - b[12]) / 20


def snap(t, beat, phase):
    return phase + round((t - phase) / beat) * beat


def build(edit):
    x = load(edit["song"])
    beat = 60 / edit["bpm"]
    parts = [[snap(a, beat, edit["phase"]), snap(b, beat, edit["phase"])] for a, b in edit["parts"]]
    parts[0][0] = max(0.0, edit["parts"][0][0])          # the opening can start anywhere
    # Each join: try the out-point of one part and the in-point of the next a few beats either way.
    for i in range(len(parts) - 1):
        best = None
        for d_out in range(-SEARCH_BEATS, SEARCH_BEATS + 1):
            for d_in in range(-SEARCH_BEATS, SEARCH_BEATS + 1):
                t_out = parts[i][1] + d_out * beat
                t_in = parts[i + 1][0] + d_in * beat
                s = similarity(chroma(x, t_out), chroma(x, t_in))
                if best is None or s > best[0]:
                    best = (s, d_out, d_in)
        s, d_out, d_in = best
        parts[i][1] += d_out * beat
        parts[i + 1][0] += d_in * beat
        print(f"  join {i}: out {parts[i][1]:.2f}s -> in {parts[i + 1][0]:.2f}s  (match {s:.2f})")

    # Exactly the edit's length: the opening gives up (or gains) the difference, never the ending.
    fades = [int(f * beat * SR) for f in edit["fades"]]
    total = sum(b - a for a, b in parts)
    parts[0][0] = max(0.0, parts[0][0] + total - edit["target"])

    out = np.zeros((0, 2))
    landed = []
    for i, (a, b) in enumerate(parts):
        fade = fades[i - 1] if i > 0 else 0
        tail = fades[i] if i < len(parts) - 1 else 0
        seg = x[int(a * SR):int(b * SR) + tail].copy()
        if i == 0:
            landed.append({"from": a, "to": b, "at": 0.0})
            out = seg
            continue
        # Equal-power crossfade over one beat, the new part starting where the old one leaves.
        k = np.linspace(0, np.pi / 2, fade)[:, None]
        out = out[:len(out)]
        start = len(out) - fade
        landed.append({"from": a, "to": b, "at": start / SR})
        mixed = out[start:] * np.cos(k) + seg[:fade] * np.sin(k)
        out = np.concatenate([out[:start], mixed, seg[fade:]])
    # Level: the loud parts to about -16 dBFS RMS, peaks kept under -1 dBFS; short fades at the ends.
    active = out[np.abs(out).mean(1) > 1e-3]
    gain = 10 ** (-16 / 20) / (np.sqrt(np.mean(active ** 2)) + 1e-12)
    out *= min(gain, 0.89 / np.abs(out).max())
    n_in, n_out = int(0.05 * SR), int(1.5 * SR)
    out[:n_in] *= np.linspace(0, 1, n_in)[:, None]
    out[-n_out:] *= np.linspace(1, 0, n_out)[:, None] ** 2
    return out, landed, beat


if __name__ == "__main__":
    index = {}
    for name, edit in EDITS.items():
        print(name)
        audio, landed, beat = build(edit)
        path = ROOT / "public" / "music" / f"edit-{name}.wav"
        with wave.open(str(path), "wb") as w:
            w.setnchannels(2)
            w.setsampwidth(2)
            w.setframerate(SR)
            w.writeframes((np.clip(audio, -1, 1) * 32767).astype("<i2").tobytes())
        index[name] = {"song": edit["song"], "duration": len(audio) / SR, "beat": beat, "parts": landed}
        print(f"  {len(audio) / SR:.2f}s, parts land at {[round(p['at'], 2) for p in landed]}")
    (ROOT / "src" / "music-edits.json").write_text(json.dumps(index, indent=2))
