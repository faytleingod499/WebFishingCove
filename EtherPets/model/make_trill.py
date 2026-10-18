# Makes the raccoon's happy trill when petted (trill.wav): a soft, purring churr, one long buzzy
# call whose syllables run together (a continuous tone dipping 15-20 times a second), rising a
# little then sinking as it trails off, like the long churrs in recordings of real raccoons.
# Run:  python3 make_trill.py out.wav
import sys
import numpy as np
from scipy.io import wavfile
from raccoon_voice import RATE, syllable, finish

rng = np.random.default_rng(11)
LEN = 1.0
t = np.arange(int(RATE * LEN)) / RATE
p = t / LEN
f_start = 1380
voice = syllable(f_start, f_start, LEN, rng, buzz=195, rasp=0.07)   # carrier, re-pitched below
# re-pitch: up a little, then down (resynthesise with the contour)
f0 = 1360 + 200 * np.sin(np.pi * np.minimum(p * 1.3, 1)) - 140 * p
f0 = f0 * (1 + 0.015 * np.sin(2 * np.pi * 7 * t))
ph = 2 * np.pi * np.cumsum(f0) / RATE
from raccoon_voice import HARM
tone = sum(a * np.sin((h + 1) * ph) for h, a in enumerate(HARM))
buzz = (0.5 + 0.5 * np.cos(2 * np.pi * np.cumsum(np.full(len(t), 195.0)) / RATE)) ** 3
tone = tone * (0.35 + 0.65 * buzz) + rng.standard_normal(len(t)) * 0.07 * (0.4 + 0.6 * buzz)
# syllables: dips at an uneven 15-20 per second, never quite to silence
rate = 17 + 3 * np.sin(2 * np.pi * 1.3 * t)
syl = 0.5 + 0.5 * np.cos(2 * np.pi * np.cumsum(rate) / RATE)
syl = 0.15 + 0.85 * syl ** 1.5
shape = np.minimum(1, t / 0.03) * np.minimum(1, (LEN - t) / 0.25) * (1 - 0.35 * p)
out = finish(tone * syl * shape)
wavfile.write(sys.argv[1] if len(sys.argv) > 1 else "trill.wav", RATE, (out * 32767).astype(np.int16))
print("trill", LEN, "s")
