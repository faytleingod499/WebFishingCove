# Makes the raccoon's chitter (chitter.wav), its answer to its owner's bark: an excited chatter of
# short, separate chirps in uneven little bursts, each chirp bending slightly, louder at first and
# trailing off, like the choppier calls in recordings of real raccoons.
# Run:  python3 make_chitter.py out.wav
import sys
import numpy as np
from scipy.io import wavfile
from raccoon_voice import RATE, call, finish

rng = np.random.default_rng(9)
LEN = 0.95
n = int(RATE * LEN)
t = np.arange(n) / RATE
f0 = np.full(n, 1540.0)
syl = []
s = 0.02
i = 0
while s < LEN - 0.06:
    dur = 0.11 if i == 0 else 0.028 + 0.022 * rng.random()        # opens with a longer note
    base = 1450 + 200 * rng.random()
    bend = rng.choice([-1, 1]) * (0.03 + 0.07 * rng.random())
    a, b = int(s * RATE), min(n, int((s + dur) * RATE))
    f0[a:b] = base * (1 + bend * np.linspace(0, 1, b - a))
    syl.append((s, dur, (0.75 + 0.25 * rng.random()) * (1 - 0.5 * (s / LEN) ** 2)))
    gap = 0.012 + 0.03 * rng.random() + (0.06 if rng.random() < 0.18 else 0.0)
    s += dur + gap
    i += 1
f0 = np.convolve(f0, np.ones(60) / 60, "same")                     # no clicks between notes
f0[f0 < 900] = 1540
out = finish(call(LEN, f0, syl, rng, flutter=95, breath=0.14), rng)
wavfile.write(sys.argv[1] if len(sys.argv) > 1 else "chitter.wav", RATE, (out * 32767).astype(np.int16))
print("chitter", i, "chirps")
