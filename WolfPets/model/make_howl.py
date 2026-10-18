# Makes the wolf's short howl (howl.wav) from scratch: a voiced tone whose pitch climbs, holds with a
# growing wobble and falls away, shaped through "oo" vowel formants, with a little breath on top.
# Run:  python3 make_howl.py out.wav
import sys
import numpy as np
from scipy.signal import butter, lfilter
from scipy.io import wavfile

RATE = 22050
LEN = 1.7
t = np.arange(int(RATE * LEN)) / RATE

# pitch contour (Hz): quick rise, slow climb while held, then a falling tail
keys_t = [0.00, 0.30, 0.95, 1.25, 1.70]
keys_f = [360., 600., 650., 560., 400.]
f0 = np.interp(t, keys_t, keys_f)
# vibrato grows through the held part
vib_depth = np.interp(t, [0.0, 0.4, 1.1, 1.7], [0.0, 0.008, 0.02, 0.012])
f0 = f0 * (1.0 + vib_depth * np.sin(2 * np.pi * 5.3 * t))

# "oo" vowel: formants around 330 Hz and 780 Hz, a weak third one
def formant_gain(freq):
    g = 0.0
    for centre, width, amp in ((330, 140, 1.0), (780, 220, 0.55), (2400, 400, 0.08)):
        g = g + amp * np.exp(-0.5 * ((freq - centre) / width) ** 2)
    return g + 0.02

phase = 2 * np.pi * np.cumsum(f0) / RATE
voice = np.zeros_like(t)
for h in range(1, 12):
    voice += formant_gain(f0 * h) / h ** 0.6 * np.sin(h * phase)

# breath: soft noise, band-passed, strongest at the start
b, a = butter(2, [500 / (RATE / 2), 3000 / (RATE / 2)], btype="band")
noise = lfilter(b, a, np.random.default_rng(7).standard_normal(len(t)))
breath = noise * np.interp(t, [0, 0.15, 0.6, 1.7], [0.35, 0.12, 0.05, 0.04])

sig = voice / np.max(np.abs(voice)) + breath
# loudness: soft start, full while held, fading tail
env = np.interp(t, [0.0, 0.12, 0.3, 1.05, 1.45, 1.7], [0.0, 0.7, 1.0, 0.95, 0.45, 0.0])
sig = sig * env
sig = sig / np.max(np.abs(sig)) * 0.85
wavfile.write(sys.argv[1] if len(sys.argv) > 1 else "howl.wav", RATE, (sig * 32767).astype(np.int16))
print("howl", len(t) / RATE, "s")
