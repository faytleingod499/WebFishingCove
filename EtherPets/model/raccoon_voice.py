# Shared voice for the raccoon's sounds, measured from a recording of real raccoons:
# - a nearly pure whistle-like tone around 1.5 kHz (overtones weak: 2nd ~24 %, 3rd+ ~4 %)
# - a rough flutter in it at roughly 70-100 Hz
# - broken into syllables about every 41 ms (36-49 ms), each a quick swell and fade
# - a little breath noise (about 7 % of the tone) and small random wobbles in pitch and loudness
import numpy as np
from scipy.signal import butter, lfilter, fftconvolve

RATE = 22050
HARM = [1.0, 0.3, 0.1, 0.03, 0.01]


def call(length, f0, syllables, rng, flutter=85.0, breath=0.07):
    """A call: f0(t) contour (array, Hz) and a list of (start, duration, loudness) syllables."""
    n = int(RATE * length)
    t = np.arange(n) / RATE
    # pitch: the contour plus slow drift and fast jitter
    drift = np.cumsum(rng.standard_normal(n)) / np.sqrt(RATE) * 0.6
    drift = np.convolve(drift, np.ones(400) / 400, "same")
    jitter = np.convolve(rng.standard_normal(n), np.ones(12) / 12, "same") * 0.05
    f = f0 * (1 + 0.01 * drift + jitter)
    ph = 2 * np.pi * np.cumsum(f) / RATE
    tone = sum(a * np.sin((h + 1) * ph + rng.random() * 6) for h, a in enumerate(HARM))
    # flutter: an irregular roughness, not a clean buzz
    fl_rate = flutter * (1 + 0.15 * np.convolve(rng.standard_normal(n), np.ones(800) / 800, "same") * 10)
    fl = 0.5 + 0.5 * np.sin(2 * np.pi * np.cumsum(fl_rate) / RATE)
    rough = np.convolve(rng.standard_normal(n), np.ones(40) / 40, "same") * 4
    tone *= np.clip(0.45 + 0.4 * fl + 0.25 * rough, 0, 1.4)
    # breath noise shaped around the tone
    nb = rng.standard_normal(n)
    b, a = butter(2, [1000 / (RATE / 2), 4500 / (RATE / 2)], btype="band")
    nb = lfilter(b, a, nb)
    nb = nb / (np.std(nb) + 1e-9) * breath * 0.7
    sig = tone + nb
    # syllables: each a fast swell and a softer fade, slightly uneven
    env = np.zeros(n)
    for start, dur, loud in syllables:
        i0 = int(start * RATE)
        m = int(dur * RATE)
        if i0 >= n:
            break
        u = np.arange(m) / m
        shape = np.sin(np.pi * np.clip(u, 0, 1) ** 0.7) ** 1.2 * loud * (1 + 0.1 * rng.standard_normal())
        env[i0:i0 + m] = np.maximum(env[i0:i0 + m], shape[: n - i0])
    return sig * env


def room(sig, rng, amount=0.12, decay=0.09):
    """A touch of small-space echo so it doesn't sound dry and synthetic."""
    m = int(RATE * decay * 3)
    ir = rng.standard_normal(m) * np.exp(-np.arange(m) / (RATE * decay))
    ir[0] = 0
    wet = fftconvolve(sig, ir)[: len(sig)]
    wet = wet / (np.max(np.abs(wet)) + 1e-9) * np.max(np.abs(sig))
    return sig + amount * wet


def finish(out, rng, lo=700, hi=5200):
    out = room(out, rng)
    b, a = butter(2, [lo / (RATE / 2), hi / (RATE / 2)], btype="band")
    out = lfilter(b, a, out)
    fade = int(RATE * 0.01)
    out[-fade:] *= np.linspace(1, 0, fade)
    return out / np.max(np.abs(out)) * 0.85
