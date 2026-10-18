# Makes the raccoon's chitter (chitter.wav), its answer to its owner's bark: a burst of short,
# separate, buzzy chirps, some sweeping up and some down, in uneven little groups like a real
# raccoon chattering, getting quicker and then trailing off.
# Run:  python3 make_chitter.py out.wav
import sys
import numpy as np
from scipy.io import wavfile
from raccoon_voice import RATE, syllable, finish

rng = np.random.default_rng(7)
LEN = 0.9
out = np.zeros(int(RATE * LEN))
t0 = 0.02
i = 0
while t0 < LEN - 0.08:
    dur = (0.16 if i == 0 else 0.03 + 0.05 * rng.random())   # opens with a longer note
    base = 1350 + 250 * rng.random()
    sweep = rng.choice([-1, 1]) * (0.04 + 0.1 * rng.random())
    s = syllable(base, base * (1 + sweep), dur, rng, buzz=200, rasp=0.16, curve=0.7)
    loud = (0.7 + 0.3 * rng.random()) * (1 - 0.55 * (t0 / LEN) ** 2)
    a = int(RATE * t0)
    out[a:a + len(s)] += s[: len(out) - a] * loud
    gap = 0.015 + 0.035 * rng.random()
    if rng.random() < 0.15:
        gap += 0.06                                   # a little pause between groups
    t0 += dur + gap
    i += 1
out = finish(out)
wavfile.write(sys.argv[1] if len(sys.argv) > 1 else "chitter.wav", RATE, (out * 32767).astype(np.int16))
print("chitter", i, "chirps")
