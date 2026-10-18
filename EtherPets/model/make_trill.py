# Makes the raccoon's happy trill when petted (trill.wav): a soft, purring churr made of quick
# syllables (about every 41 ms, like the real ones) that run together, rising a touch and then
# sinking and slowing as it trails off.
# Run:  python3 make_trill.py out.wav
import sys
import numpy as np
from scipy.io import wavfile
from raccoon_voice import RATE, call, finish

rng = np.random.default_rng(21)
LEN = 1.0
t = np.arange(int(RATE * LEN)) / RATE
p = t / LEN
f0 = 1470 + 110 * np.sin(np.pi * np.minimum(p * 1.4, 1)) - 130 * p
syl = []
s = 0.0
while s < LEN - 0.05:
    q = s / LEN
    gap = 0.039 + 0.006 * rng.random() + 0.025 * q ** 2          # slows down at the end
    loud = (0.6 + 0.4 * np.sin(np.pi * min(q * 1.6, 1))) * (1 - 0.6 * q ** 2)
    syl.append((s, gap * 0.85, loud))                              # nearly touching the next one
    s += gap
out = finish(call(LEN, f0, syl, rng, flutter=80, breath=0.12), rng)
wavfile.write(sys.argv[1] if len(sys.argv) > 1 else "trill.wav", RATE, (out * 32767).astype(np.int16))
print("trill", len(syl), "syllables")
