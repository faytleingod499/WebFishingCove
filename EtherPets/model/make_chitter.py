# Makes the raccoon's chitter (chitter.wav), its answer to its owner's bark: an excited, rapid-fire
# chatter - quick bursts of very short chirps (25-30 a second) that hop up and down in pitch,
# with tiny tooth-clicks between them and brief breaths between bursts, trailing off at the end.
# Run:  python3 make_chitter.py out.wav
import sys
import numpy as np
from scipy.io import wavfile
from raccoon_voice import RATE, call, finish

rng = np.random.default_rng(14)
LEN = 1.2
n = int(RATE * LEN)
f0 = np.full(n, 1540.0)
syl = []
clicks = np.zeros(n)
s = 0.02
i = 0
burst_left = int(rng.integers(4, 8))
while s < LEN - 0.05:
    q = s / LEN
    dur = 0.016 + 0.014 * rng.random()
    base = 1380 + 380 * rng.random()                     # hops around from chirp to chirp
    bend = rng.choice([-1, 1]) * (0.05 + 0.1 * rng.random())
    a, b = int(s * RATE), min(n, int((s + dur) * RATE))
    f0[a:b] = base * (1 + bend * np.linspace(0, 1, b - a))
    syl.append((s, dur, (0.7 + 0.3 * rng.random()) * (1 - 0.55 * q ** 2)))
    # a tiny click at the start of most chirps, like teeth chattering
    if rng.random() < 0.7 and a + 40 < n:
        clicks[a:a + 40] += rng.standard_normal(40) * np.exp(-np.arange(40) / 6) * 0.5
    s += dur + 0.008 + 0.012 * rng.random()
    burst_left -= 1
    if burst_left <= 0:                                  # a breath between bursts
        s += 0.05 + 0.06 * rng.random()
        burst_left = int(rng.integers(4, 9))
    i += 1
f0 = np.convolve(f0, np.ones(25) / 25, "same")
out = call(LEN, f0, syl, rng, flutter=95, breath=0.14) + clicks
out = finish(out, rng)
wavfile.write(sys.argv[1] if len(sys.argv) > 1 else "chitter.wav", RATE, (out * 32767).astype(np.int16))
print("chitter", i, "chirps")
