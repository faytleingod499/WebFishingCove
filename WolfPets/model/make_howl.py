# Makes the wolf's short howl (howl.wav) from scratch.
# A real howl is close to a pure tone: a low scoop up into a long, slightly wandering held note that
# falls away at the end. So this is mostly one sine with a few soft overtones, a pitch that drifts a
# little at random (no machine-regular wobble), an "oo" that opens to "ow" and back, a short breath
# at the very start, and a touch of outdoor echo.
# Run:  python3 make_howl.py out.wav
import sys
import numpy as np
from scipy.signal import butter, lfilter, fftconvolve
from scipy.io import wavfile

RATE = 22050
LEN = 2.0
rng = np.random.default_rng(11)
n = int(RATE * LEN)
t = np.arange(n) / RATE


def smooth_noise(rate_hz, amount):
    # slow random wandering, for a living pitch and loudness
    pts = int(LEN * rate_hz) + 3
    knots = rng.standard_normal(pts) * amount
    return np.interp(t, np.linspace(0, LEN, pts), knots)


# pitch: scoop up from low, hold around 460 Hz with a slow climb, then fall away
keys_t = [0.00, 0.22, 0.45, 1.05, 1.35, 1.75, 2.00]
keys_f = [250., 400., 455., 475., 465., 360., 300.]
f0 = np.interp(t, keys_t, keys_f)
f0 *= 1.0 + smooth_noise(6, 0.006)                       # tiny natural unsteadiness
late = np.clip((t - 0.7) / 0.6, 0, 1) * np.clip((1.8 - t) / 0.3, 0, 1)
f0 *= 1.0 + 0.004 * late * np.sin(2 * np.pi * 4.6 * t)    # a faint wobble only while held
phase = 2 * np.pi * np.cumsum(f0) / RATE

# the vowel opens ("oo" -> "ow") as it rises and closes again as it falls
open_amt = np.interp(t, [0.0, 0.4, 1.1, 1.6, 2.0], [0.15, 0.55, 0.65, 0.3, 0.1])
voice = (np.sin(phase)
         + open_amt * 0.45 * np.sin(2 * phase + 0.3)
         + open_amt * 0.18 * np.sin(3 * phase + 0.9)
         + open_amt * 0.06 * np.sin(4 * phase + 1.7))

# loudness: swell in, hold with a little life, fade with the falling pitch
env = np.interp(t, [0.0, 0.10, 0.35, 1.2, 1.6, 2.0], [0.0, 0.45, 0.95, 1.0, 0.55, 0.0])
env *= 1.0 + smooth_noise(4, 0.05)
sig = voice * env

# a soft breath only as it starts
b, a = butter(2, [300 / (RATE / 2), 1800 / (RATE / 2)], btype="band")
breath = lfilter(b, a, rng.standard_normal(n)) * np.interp(t, [0, 0.05, 0.25, 0.4], [0.0, 0.25, 0.05, 0.0])
sig = sig + breath

# take the edge off and add an outdoor echo tail
b, a = butter(2, 2500 / (RATE / 2))
sig = lfilter(b, a, sig)
tail = int(RATE * 0.8)
impulse = rng.standard_normal(tail) * np.exp(-np.arange(tail) / (RATE * 0.18))
impulse[0] = 0
wet = fftconvolve(sig, impulse)[:n] * 0.04
sig = sig + wet
fade = np.clip((LEN - t) / 0.05, 0, 1)
sig = sig * fade
sig = sig / np.max(np.abs(sig)) * 0.85
wavfile.write(sys.argv[1] if len(sys.argv) > 1 else "howl.wav", RATE, (sig * 32767).astype(np.int16))
print("howl", LEN, "s")
