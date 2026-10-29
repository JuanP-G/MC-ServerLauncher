"""Composes the promo's music and sound effects from nothing but code, so every note is ours.

The arrangement follows src/edits.json: the same sections the video cuts on, at the same tempo, so
each drum hit lands on a cut. Writes public/audio/music-<edit>.wav and public/audio/sfx/*.wav.

    python audio/compose.py            # every edit and the effects
    python audio/compose.py promo45    # one edit
"""
import json
import sys
import wave
from pathlib import Path

import numpy as np
from scipy import signal

SR = 48000
ROOT = Path(__file__).resolve().parent.parent
EDITS = json.loads((ROOT / "src" / "edits.json").read_text(encoding="utf-8"))
BPM = EDITS["bpm"]
BEAT = 60.0 / BPM
RNG = np.random.default_rng(7)


# ---------------------------------------------------------------- basics
def secs(n):
    return int(round(n * SR))


def midi_hz(m):
    return 440.0 * 2 ** ((m - 69) / 12)


def env_adsr(n, a=0.005, d=0.1, s=0.7, r=0.05):
    a, d, r = secs(a), secs(d), secs(r)
    e = np.full(n, s, dtype=np.float64)
    a = min(a, n)
    e[:a] = np.linspace(0, 1, a, endpoint=False)
    dd = min(d, max(0, n - a))
    e[a:a + dd] = np.linspace(1, s, dd, endpoint=False)
    rr = min(r, n)
    e[n - rr:] *= np.linspace(1, 0, rr)
    return e


def _blep(ph, dt):
    """PolyBLEP: rounds off each jump of a saw or square so it does not alias into harsh tones."""
    y = np.zeros_like(ph)
    lo = ph < dt
    x = ph[lo] / dt[lo]
    y[lo] = x + x - x * x - 1
    hi = ph > 1 - dt
    x = (ph[hi] - 1) / dt[hi]
    y[hi] = x * x + x + x + 1
    return y


def osc(kind, f, n, phase=0.0, duty=0.5):
    t = np.arange(n) / SR
    ph = (f * t + phase) % 1.0 if np.isscalar(f) else (np.cumsum(f) / SR + phase) % 1.0
    dt = np.broadcast_to(np.asarray(f, dtype=float) / SR, (n,)).copy()
    if kind == "sin":
        return np.sin(2 * np.pi * ph)
    if kind == "saw":
        return 2 * ph - 1 - _blep(ph, dt)
    if kind == "square":
        return np.where(ph < duty, 1.0, -1.0) + _blep(ph, dt) - _blep((ph + 1 - duty) % 1.0, dt)
    if kind == "tri":
        return 2 * np.abs(2 * ph - 1) - 1
    raise ValueError(kind)


def lowpass(x, cutoff, order=2):
    b, a = signal.butter(order, min(cutoff, SR * 0.45) / (SR / 2), "low")
    return signal.lfilter(b, a, x)


def highpass(x, cutoff, order=2):
    b, a = signal.butter(order, cutoff / (SR / 2), "high")
    return signal.lfilter(b, a, x)


def bandpass(x, lo, hi, order=2):
    b, a = signal.butter(order, [lo / (SR / 2), min(hi, SR * 0.45) / (SR / 2)], "band")
    return signal.lfilter(b, a, x)


def sweep_lowpass(x, f0, f1):
    """A low-pass whose cutoff glides from f0 to f1, done in blocks."""
    out = np.zeros_like(x)
    block = 1024
    zi = None
    cuts = np.geomspace(max(f0, 40), max(f1, 40), max(1, len(x) // block + 1))
    for k, start in enumerate(range(0, len(x), block)):
        b, a = signal.butter(2, min(cuts[k], SR * 0.45) / (SR / 2), "low")
        if zi is None:
            zi = signal.lfilter_zi(b, a) * 0
        out[start:start + block], zi = signal.lfilter(b, a, x[start:start + block], zi=zi)
    return out


class Bus:
    """A stereo track: notes are added at a time with a pan, and it can send to the reverb."""

    def __init__(self, n, send=0.0):
        self.L = np.zeros(n)
        self.R = np.zeros(n)
        self.send = send

    def add(self, t, x, gain=1.0, pan=0.0):
        i = secs(t)
        if i >= len(self.L) or i + len(x) <= 0:
            return
        if i < 0:
            x, i = x[-i:], 0
        x = x[: len(self.L) - i] * gain
        self.L[i:i + len(x)] += x * np.sqrt(0.5 * (1 - pan))
        self.R[i:i + len(x)] += x * np.sqrt(0.5 * (1 + pan))


# ---------------------------------------------------------------- instruments
def kick(punch=1.0):
    n = secs(0.42)
    t = np.arange(n) / SR
    f = 46 + 120 * np.exp(-t * 32)
    body = np.sin(2 * np.pi * np.cumsum(f) / SR) * np.exp(-t * 7.5)
    click = highpass(RNG.standard_normal(n) * np.exp(-t * 400), 2500) * 0.35
    return np.tanh((body + click) * 1.6 * punch) * 0.95


def snare():
    n = secs(0.3)
    t = np.arange(n) / SR
    tone = np.sin(2 * np.pi * 190 * t) * np.exp(-t * 28)
    noise = bandpass(RNG.standard_normal(n), 1200, 9000) * np.exp(-t * 16)
    return (tone * 0.55 + noise * 0.9) * 0.8


def clap():
    n = secs(0.35)
    t = np.arange(n) / SR
    e = np.zeros(n)
    for k, off in enumerate([0, 0.011, 0.022, 0.034]):
        i = secs(off)
        e[i:] += np.exp(-(t[: n - i]) * (90 if k < 3 else 14))
    return bandpass(RNG.standard_normal(n), 900, 7000) * e * 0.7


def hat(open_=False):
    n = secs(0.32 if open_ else 0.06)
    t = np.arange(n) / SR
    x = highpass(RNG.standard_normal(n), 7000, 4) * np.exp(-t * (9 if open_ else 70))
    return x * (0.32 if open_ else 0.3)


def crash():
    n = secs(2.4)
    t = np.arange(n) / SR
    x = highpass(RNG.standard_normal(n), 4500, 2) * np.exp(-t * 1.6)
    return x * 0.35


def sub_boom():
    n = secs(1.8)
    t = np.arange(n) / SR
    f = 34 + 60 * np.exp(-t * 6)
    return np.sin(2 * np.pi * np.cumsum(f) / SR) * np.exp(-t * 2.2) * 0.9


def pulse_note(m, dur, duty=0.25, vib=0.0, gain=0.25, decay=0.0):
    n = secs(dur)
    t = np.arange(n) / SR
    f = midi_hz(m) * (1 + vib * np.sin(2 * np.pi * 5.5 * t) * np.clip((t - 0.08) * 6, 0, 1))
    x = osc("square", f, n, duty=duty)
    e = env_adsr(n, 0.002, 0.06, 0.8, 0.02)
    if decay:
        e *= np.exp(-t * decay)
    return x * e * gain


def supersaw(ms, dur, cutoff=3200, gain=0.12):
    n = secs(dur)
    x = np.zeros(n)
    for m in ms:
        for d in (-0.13, -0.07, 0, 0.07, 0.13):
            x += osc("saw", midi_hz(m + d), n, phase=RNG.random())
    x = lowpass(x, cutoff)
    return x * env_adsr(n, 0.02, 0.2, 0.85, 0.12) * gain / len(ms)


def bass_note(m, dur, cutoff=900, gain=0.42):
    n = secs(dur)
    x = osc("saw", midi_hz(m), n) * 0.6 + osc("square", midi_hz(m - 12), n, duty=0.5) * 0.5
    x = lowpass(x, cutoff, 2)
    return np.tanh(x * 1.4) * env_adsr(n, 0.003, 0.08, 0.8, 0.03) * gain


def reese(m, dur, gain=0.35):
    n = secs(dur)
    x = osc("saw", midi_hz(m) * 0.995, n) + osc("saw", midi_hz(m) * 1.006, n) + osc("saw", midi_hz(m - 12), n) * 0.7
    x = lowpass(x, 420)
    return np.tanh(x * 2.2) * env_adsr(n, 0.004, 0.05, 0.9, 0.04) * gain


def noise_riser(dur, gain=0.35):
    n = secs(dur)
    x = RNG.standard_normal(n)
    x = sweep_lowpass(highpass(x, 300), 400, 14000)
    e = np.linspace(0, 1, n) ** 2.2
    return x * e * gain


def reverse_swell(dur, gain=0.4):
    return crash()[: secs(dur)][::-1] * np.linspace(0.2, 1, secs(dur)) * gain / 0.35


def tape_stop(x, dur):
    """The end of x played slower and slower, like a record stopped by hand."""
    n = secs(dur)
    seg = x[-n:] if len(x) >= n else x
    speed = np.linspace(1.0, 0.0, n) ** 1.3
    pos = np.cumsum(speed)
    pos = np.clip(pos, 0, len(seg) - 1)
    return np.interp(pos, np.arange(len(seg)), seg) * np.linspace(1, 0, n)


def bitcrush(x, bits=5, down=6):
    y = np.round(x * 2 ** bits) / 2 ** bits
    return np.repeat(y[::down], down)[: len(x)]


def reverb_ir(seconds=1.9, decay=3.2):
    n = secs(seconds)
    t = np.arange(n) / SR
    base = RNG.standard_normal(n) * np.exp(-t * decay)
    l = lowpass(base, 6500)
    r = lowpass(np.roll(base, 331) * 0.98, 6000)
    return l / np.abs(l).sum() * 40, r / np.abs(r).sum() * 40


# ---------------------------------------------------------------- music
PROG = [(57, [57, 60, 64]), (53, [53, 57, 60]), (48, [48, 52, 55, 60]), (55, [55, 59, 62])]   # Am F C G
# The hook, in beats over the four chords: (beat, length, midi).
HOOK = [
    (0, .5, 76), (.5, .5, 81), (1, .5, 79), (1.5, .5, 76), (2, .5, 74), (2.5, 1.5, 76),
    (4, .5, 72), (4.5, .5, 74), (5, .5, 76), (5.5, .5, 77), (6, 1, 76), (7, .5, 72), (7.5, .5, 69),
    (8, .5, 67), (8.5, .5, 72), (9, .5, 76), (9.5, .5, 79), (10, 1, 81), (11, .5, 79), (11.5, .5, 76),
    (12, .5, 74), (12.5, .5, 76), (13, 1, 79), (14, .5, 83), (14.5, 1.5, 81),
]


def kind_of(section):
    if section == "chaos":
        return "chaos"
    if section == "click":
        return "silence"
    if section == "dual":
        return "break"
    if section == "outro":
        return "outro"
    if section in ("community", "wall"):
        return "peak"
    return "drop"


def compose(edit_name):
    edit = EDITS["edits"][edit_name]
    total_beats = sum(b for _, b in edit["sections"])
    n = secs(total_beats * BEAT + edit["tail"] + 2.5)
    drums, bass, music, lead, fx = Bus(n, 0.05), Bus(n, 0.0), Bus(n, 0.25), Bus(n, 0.3), Bus(n, 0.2)
    kicks = []
    k_s, sn_s, cl_s, hc_s, ho_s, cr_s = kick(), snare(), clap(), hat(), hat(True), crash()

    beat0 = 0
    groove_beat = 0          # beats of music since the first drop: keeps the chords in step
    sections = edit["sections"]
    for si, (name, beats) in enumerate(sections):
        kind = kind_of(name)
        t0 = beat0 * BEAT
        nxt = kind_of(sections[si + 1][0]) if si + 1 < len(sections) else None

        if kind == "chaos":
            # A detuned bass that pulses faster as the cuts speed up, ticking hats, glitchy stabs.
            for b in range(beats):
                t = t0 + b * BEAT
                prog = b / max(1, beats - 1)
                sub = 2 if prog < 0.34 else (4 if prog < 0.7 else 8)
                for k in range(sub):
                    bass.add(t + k * BEAT / sub, reese(40 if (b // 4) % 2 == 0 else 41, BEAT / sub * 0.9), 0.9)
                for k in range(4):
                    drums.add(t + k * BEAT / 4, hc_s, 0.3 + 0.25 * (k % 2), pan=0.3 * (-1) ** k)
                if b % 4 == 0 or prog > 0.5:
                    drums.add(t, k_s, 0.7)
                    kicks.append(t)
                stab = sum(pulse_note(m, 0.18, duty=0.125, gain=0.22) for m in (64, 65, 70))
                fx.add(t, lowpass(bitcrush(stab, 4, 8), 5000), 0.8, pan=RNG.uniform(-0.6, 0.6))
            riser_len = min(beats, 6) * BEAT
            fx.add(t0 + beats * BEAT - riser_len, noise_riser(riser_len), 0.8)
        elif kind == "silence":
            # One breath: the tail of the chaos stops like a tape, then a swell sucks into the drop.
            stop = tape_stop(bass.L[: secs(t0)] + bass.R[: secs(t0)], 0.5)
            fx.add(t0, stop, 0.8)
            sw = min(beats, 2) * BEAT
            fx.add(t0 + beats * BEAT - sw, reverse_swell(sw), 0.7)
        elif kind in ("drop", "peak", "break"):
            first = beat0 == 0 or kind_of(sections[si - 1][0]) not in ("drop", "peak", "break")
            if first and kind != "break":
                drums.add(t0, cr_s, 1.0, pan=0.2)
                fx.add(t0, sub_boom(), 0.9)
            for b in range(beats):
                t = t0 + b * BEAT
                gb = groove_beat + b
                root, chord = PROG[(gb // 4) % 4]
                bar_beat = gb % 4
                if kind != "break":
                    drums.add(t, k_s, 1.0)
                    kicks.append(t)
                    drums.add(t + BEAT / 2, ho_s, 0.8, pan=0.25)
                    for k in (1, 3):
                        drums.add(t + k * BEAT / 4, hc_s, 0.55, pan=-0.3)
                    if bar_beat in (1, 3):
                        drums.add(t, cl_s, 0.95)
                        drums.add(t, sn_s, 0.45)
                    for k in range(2):
                        bass.add(t + k * BEAT / 2, bass_note(root - 12 + (12 if k == 1 and b % 2 else 0), BEAT / 2 * 0.92), 1.0)
                else:
                    # Half time: a clap on the third beat, no kick, a roll into what comes next.
                    if bar_beat == 2:
                        drums.add(t, cl_s, 0.9)
                    drums.add(t + BEAT / 2, hc_s, 0.5)
                    if b >= beats - 2:
                        roll = 4 if b == beats - 2 else 8
                        for k in range(roll):
                            drums.add(t + k * BEAT / roll, sn_s, 0.25 + 0.5 * (k / roll) + 0.2 * (b - beats + 2))
                    if bar_beat == 0:
                        bass.add(t, bass_note(root - 12, BEAT * 3.8, cutoff=500), 0.8)
                # The chiptune arpeggio, two octaves up, every sixteenth.
                arp = chord + [chord[0] + 12]
                for k in range(4):
                    m = arp[(gb * 4 + k) % len(arp)] + 12
                    lead.add(t + k * BEAT / 4, pulse_note(m, BEAT / 4 * 0.9, duty=0.125, gain=0.11, decay=6), 1.0,
                             pan=0.35 * (-1) ** k)
                if bar_beat == 0:
                    pad = supersaw([c + 12 for c in chord], BEAT * 4 * 0.98, cutoff=1400 if kind == "break" else 3600)
                    music.add(t, pad, 1.0 if kind != "break" else 0.8)
            # The hook over each four-bar phrase that has room for it.
            if kind != "break":
                for (hb, hl, hm) in HOOK:
                    g = beat0 + hb
                    phrase_start = groove_beat - groove_beat % 16
                    for ph in range(phrase_start, groove_beat + beats, 16):
                        b_abs = ph + hb
                        if groove_beat <= b_abs < groove_beat + beats:
                            tt = t0 + (b_abs - groove_beat) * BEAT
                            lead.add(tt, pulse_note(hm, hl * BEAT * 0.95, duty=0.25, vib=0.006, gain=0.2), 1.0)
                            if kind == "peak":
                                lead.add(tt, pulse_note(hm + 12, hl * BEAT * 0.95, duty=0.5, gain=0.07), 1.0, pan=0.4)
            if kind == "break" or nxt in ("silence",):
                fx.add(t0 + beats * BEAT - min(beats, 4) * BEAT, noise_riser(min(beats, 4) * BEAT, 0.3), 0.9)
            groove_beat += beats
        elif kind == "outro":
            # The last hit, then the chord rings out with a slow arpeggio that dies away.
            drums.add(t0, k_s, 1.1)
            kicks.append(t0)
            drums.add(t0, cr_s, 1.1)
            fx.add(t0, sub_boom(), 1.0)
            ring = beats * BEAT + edit["tail"] + 1.2
            music.add(t0, supersaw([57 + 12, 60 + 12, 64 + 12, 71 + 12], ring, cutoff=2600, gain=0.16), 1.0)
            bass.add(t0, bass_note(45 - 12, min(ring, 3.2), cutoff=400), 0.9)
            arp = [69, 72, 76, 81, 83, 81, 76, 72]
            for k in range(int(beats * 2)):
                tt = t0 + k * BEAT / 2
                g = 0.12 * np.exp(-k / (beats * 0.9))
                lead.add(tt, pulse_note(arp[k % len(arp)] + 12, BEAT / 2 * 0.9, duty=0.125, gain=g, decay=4), 1.0,
                         pan=0.4 * (-1) ** k)
            # A two-note "level up" sting where the download button lands.
            sting_t = t0 + min(beats, 6) * BEAT
            for k, m in enumerate((88, 93)):
                lead.add(sting_t + k * 0.09, pulse_note(m, 0.5, duty=0.5, gain=0.12, decay=5), 1.0)
        beat0 += beats

    # Sidechain: everything melodic ducks under each kick, the pumping of dance music.
    t = np.arange(n) / SR
    duck = np.ones(n)
    for tk in kicks:
        i = secs(tk)
        j = min(n, i + secs(0.3))
        duck[i:j] = np.minimum(duck[i:j], 1 - 0.65 * np.exp(-(t[i:j] - tk) / 0.07))
    for bus in (bass, music, lead):
        bus.L *= duck
        bus.R *= duck

    irl, irr = reverb_ir()
    sendL = sum(b.L * b.send for b in (drums, bass, music, lead, fx))
    sendR = sum(b.R * b.send for b in (drums, bass, music, lead, fx))
    revL = signal.fftconvolve(sendL, irl)[:n]
    revR = signal.fftconvolve(sendR, irr)[:n]
    L = drums.L + bass.L + music.L + lead.L * 0.9 + fx.L + revL * 0.5
    R = drums.R + bass.R + music.R + lead.R * 0.9 + fx.R + revR * 0.5

    # Master: tame the lows, glue with a gentle soft clip, then end exactly when the video does.
    L, R = highpass(L, 28), highpass(R, 28)
    peak = max(np.abs(L).max(), np.abs(R).max())
    L, R = np.tanh(L / peak * 1.5) / np.tanh(1.5), np.tanh(R / peak * 1.5) / np.tanh(1.5)
    length = secs(total_beats * BEAT + edit["tail"])
    L, R = L[:length], R[:length]
    fade = secs(0.15)
    L[-fade:] *= np.linspace(1, 0, fade)
    R[-fade:] *= np.linspace(1, 0, fade)
    return np.stack([L, R], axis=1) * 0.89


# ---------------------------------------------------------------- sound effects
def sfx():
    out = {}
    n = secs(0.09)
    t = np.arange(n) / SR
    # Mouse click: two tiny ticks, press and release.
    tick = highpass(RNG.standard_normal(n), 2000) * np.exp(-t * 300)
    click = tick.copy()
    click[secs(0.045):] += tick[: n - secs(0.045)] * 0.6
    out["click"] = click * 0.8
    # The big click that ends the chaos: a click plus a low thump and a little glass.
    n2 = secs(0.7)
    t2 = np.arange(n2) / SR
    thump = np.sin(2 * np.pi * (60 + 90 * np.exp(-t2 * 30)) * t2) * np.exp(-t2 * 9)
    glass = sum(np.sin(2 * np.pi * f * t2) * np.exp(-t2 * d) for f, d in ((2637, 9), (3520, 11), (5274, 14))) * 0.15
    big = thump * 0.9 + glass
    big[: len(click)] += click
    out["bigclick"] = big
    # Whoosh: band-passed noise that rises and falls.
    n3 = secs(0.45)
    w = sweep_lowpass(highpass(RNG.standard_normal(n3), 400), 900, 9000)
    out["whoosh"] = w * np.sin(np.linspace(0, np.pi, n3)) ** 2 * 0.6
    # Block placed: a short wooden knock.
    n4 = secs(0.16)
    t4 = np.arange(n4) / SR
    knock = bandpass(RNG.standard_normal(n4), 180, 1800) * np.exp(-t4 * 40) + np.sin(2 * np.pi * 140 * t4) * np.exp(-t4 * 35) * 0.6
    out["block"] = knock * 0.8
    # Pop for chips and ticks.
    n5 = secs(0.12)
    t5 = np.arange(n5) / SR
    out["pop"] = np.sin(2 * np.pi * (500 + 900 * np.exp(-t5 * 50)) * t5) * np.exp(-t5 * 30) * 0.5
    # Keyboard: one key.
    n6 = secs(0.05)
    t6 = np.arange(n6) / SR
    out["key"] = bandpass(RNG.standard_normal(n6), 1500, 7000) * np.exp(-t6 * 160) * 0.5
    # Error buzz for the chaos windows.
    n7 = secs(0.22)
    out["error"] = bitcrush(osc("square", 110, n7) * 0.3 + osc("square", 155.6, n7) * 0.25, 3, 10) * env_adsr(n7, 0.002, 0.05, 0.9, 0.03)
    # Glitch: chopped noise and tones.
    n8 = secs(0.3)
    g = np.zeros(n8)
    for k in range(6):
        i = secs(k * 0.05)
        seg = osc("square", RNG.uniform(200, 1600), secs(0.03)) * 0.3 if k % 2 else RNG.standard_normal(secs(0.03)) * 0.25
        g[i:i + len(seg)] += seg
    out["glitch"] = bitcrush(g, 4, 4)
    # Level-up ding for counters and the end.
    n9 = secs(0.6)
    t9 = np.arange(n9) / SR
    out["ding"] = sum(np.sin(2 * np.pi * midi_hz(m) * t9) * np.exp(-t9 * 6) * a for m, a in ((88, 0.3), (100, 0.12)))
    # The microwave's bell: two struck tones, a little detuned, ringing out.
    n10 = secs(1.6)
    t10 = np.arange(n10) / SR
    bell = sum(np.sin(2 * np.pi * f * t10) * np.exp(-t10 * d) * a for f, d, a in ((1318.5, 2.2, 0.5), (1325, 2.4, 0.3), (2637, 4.5, 0.18), (3951, 7, 0.08)))
    out["microwave"] = bell * env_adsr(n10, 0.002, 0.05, 1.0, 0.2) * 0.8
    return out


def write_wav(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    if data.ndim == 1:
        data = np.stack([data, data], axis=1)
    pcm = (np.clip(data, -1, 1) * 32767).astype("<i2")
    with wave.open(str(path), "wb") as w:
        w.setnchannels(2)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm.tobytes())


if __name__ == "__main__":
    out = ROOT / "public" / "audio"
    names = sys.argv[1:] or list(EDITS["edits"])
    for name in names:
        mix = compose(name)
        write_wav(out / f"music-{name}.wav", mix)
        print(f"music-{name}.wav  {len(mix) / SR:.2f}s")
    if len(sys.argv) == 1:
        for name, x in sfx().items():
            write_wav(out / "sfx" / f"{name}.wav", x)
        print("sfx done")
