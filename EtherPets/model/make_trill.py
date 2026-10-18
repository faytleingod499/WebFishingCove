# Makes the raccoon's happy sound when petted (trill.wav): a soft, contented chatter - quick runs
# of short chirps (about 25 a second) with gentler pitch hops than the chitter, a few soft
# tooth-clicks, and longer, smoother runs that rise a little and then settle and slow down.
# Run:  python3 make_trill.py out.wav
import sys
import numpy as np
from scipy.io import wavfile
from raccoon_voice import RATE, call, finish

rng = np.random.default_rng(31)
LEN = 1.1
n = int(RATE * LEN)
f0 = np.full(n, 1480.0)
syl = []
clicks = np.zeros(n)
s = 0.02
i = 0
run_left = int(rng.integers(7, 11))
while s < LEN - 0.05:
    q = s / LEN
    dur = 0.022 + 0.012 * rng.random()
    base = 1400 + 160 * np.sin(np.pi * min(q * 1.5, 1)) - 120 * q + 140 * rng.random()   # gentle hops
    bend = rng.choice([-1, 1]) * (0.03 + 0.05 * rng.random())
    a, b = int(s * RATE), min(n, int((s + dur) * RATE))
    f0[a:b] = base * (1 + bend * np.linspace(0, 1, b - a))
    syl.append((s, dur, (0.65 + 0.25 * rng.random()) * (1 - 0.6 * q ** 2)))
    if rng.random() < 0.35 and a + 40 < n:
        clicks[a:a + 40] += rng.standard_normal(40) * np.exp(-np.arange(40) / 6) * 0.25
    s += dur + 0.008 + 0.01 * rng.random() + 0.02 * q ** 2        # slows as it settles
    run_left -= 1
    if run_left <= 0:                                             # a short contented breath
        s += 0.04 + 0.04 * rng.random()
        run_left = int(rng.integers(7, 12))
    i += 1
f0 = np.convolve(f0, np.ones(25) / 25, "same")
out = call(LEN, f0, syl, rng, flutter=80, breath=0.12) + clicks
out = finish(out, rng)
wavfile.write(sys.argv[1] if len(sys.argv) > 1 else "trill.wav", RATE, (out * 32767).astype(np.int16))
print("trill", i, "chirps")
