# Shared voice for the raccoon's sounds, modelled on recordings of real raccoons:
# a reedy tone around 1.4-1.6 kHz with strong harmonics (about 2.9 and 4.4 kHz), buzzed by
# fast pulses (~200 a second) and broken into syllables 15-25 times a second.
import numpy as np
from scipy.signal import butter, lfilter

RATE = 22050
HARM = [1.0, 0.7, 0.3, 0.14, 0.06]


def syllable(f0_start, f0_end, dur, rng, buzz=200.0, rasp=0.12, curve=1.0):
    """One buzzy syllable gliding from f0_start to f0_end."""
    n = int(RATE * dur)
    t = np.arange(n) / RATE
    k = (t / dur) ** curve
    f0 = f0_start + (f0_end - f0_start) * k
    f0 = f0 * (1 + 0.012 * np.sin(2 * np.pi * 9 * t + rng.random() * 6))   # a little waver
    ph = 2 * np.pi * np.cumsum(f0) / RATE
    tone = sum(a * np.sin((h + 1) * ph + rng.random()) for h, a in enumerate(HARM))
    # the buzz: sharp pulses, not a smooth tremolo
    bp = 2 * np.pi * np.cumsum(np.full(n, buzz * (0.95 + 0.1 * rng.random()))) / RATE
    pulses = (0.5 + 0.5 * np.cos(bp)) ** 3
    tone = tone * (0.35 + 0.65 * pulses)
    noise = rng.standard_normal(n) * rasp * (0.4 + 0.6 * pulses)
    env = np.minimum(1, t / 0.008) * np.minimum(1, (dur - t) / 0.012)
    return (tone + noise) * np.clip(env, 0, 1)


def finish(out, lo=900, hi=7000):
    b, a = butter(2, [lo / (RATE / 2), hi / (RATE / 2)], btype="band")
    out = lfilter(b, a, out)
    return out / np.max(np.abs(out)) * 0.85
