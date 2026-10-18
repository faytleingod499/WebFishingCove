# Makes the raccoon's chitter (chitter.wav): a quick run of little squeaky chirps that rise a touch
# in pitch, each a short upward sweep with a soft rasp, the run speeding up and fading at the end.
# Run:  python3 make_chitter.py out.wav
import sys
import numpy as np
from scipy.signal import butter, lfilter
from scipy.io import wavfile

RATE = 22050
rng = np.random.default_rng(5)
LEN = 0.85
out = np.zeros(int(RATE * LEN))

t0 = 0.02
i = 0
while t0 < LEN - 0.08:
    dur = 0.045 + 0.012 * rng.random()
    n = int(RATE * dur)
    t = np.arange(n) / RATE
    base = 1500 + 90 * i + 120 * rng.random()          # each chirp a bit higher than the last
    f = base * (1.0 + 0.45 * (t / dur) ** 0.8)          # a quick upward sweep
    ph = 2 * np.pi * np.cumsum(f) / RATE
    tone = np.sin(ph) + 0.35 * np.sin(2 * ph) + 0.12 * np.sin(3 * ph)
    rasp = rng.standard_normal(n) * 0.25
    env = np.sin(np.pi * np.clip(t / dur, 0, 1)) ** 1.5
    loud = 1.0 - 0.6 * (t0 / LEN) ** 2
    chirp = (tone + rasp) * env * loud
    a = int(RATE * t0)
    out[a:a + n] += chirp[: len(out) - a]
    t0 += dur + max(0.012, 0.05 - 0.004 * i) * (0.7 + 0.6 * rng.random())
    i += 1

b, a = butter(2, [700 / (RATE / 2), 6000 / (RATE / 2)], btype="band")
out = lfilter(b, a, out)
out = out / np.max(np.abs(out)) * 0.8
wavfile.write(sys.argv[1] if len(sys.argv) > 1 else "chitter.wav", RATE, (out * 32767).astype(np.int16))
print("chitter", i, "chirps")
