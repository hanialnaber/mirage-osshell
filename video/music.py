# Soft ambient pad + transition swells for the promo. Writes music.wav (48 kHz stereo).
import numpy as np, wave, sys
SR = 48000; DUR = float(sys.argv[1]) if len(sys.argv) > 1 else 48.0
n = int(SR * DUR); t = np.arange(n) / SR
def note(m): return 440 * 2 ** ((m - 69) / 12)
# Cmaj9 - Am9 - Fmaj7#11 - Gsus, slow
chords = [[48, 55, 59, 62, 64], [45, 52, 55, 59, 64], [41, 48, 52, 57, 59], [43, 50, 55, 57, 62]]
seg = DUR / 8
out = np.zeros((n, 2))
rng = np.random.default_rng(7)
for i in range(8):
    c = chords[i % 4]
    a, b = int(i * seg * SR), int(min(n, (i + 1) * seg * SR + 2.5 * SR))
    tt = t[a:b] - t[a]; L = len(tt)
    env = np.minimum(1, tt / 1.6) * np.minimum(1, np.maximum(0, (L / SR - tt)) / 2.5)
    for k, m in enumerate(c):
        f = note(m + 12)
        for det, pan in ((-0.12, 0.3), (0.12, 0.7)):
            ph = rng.uniform(0, 6.28)
            w = np.sin(2 * np.pi * f * (1 + det / 100) * tt + ph) + 0.25 * np.sin(2 * np.pi * 2 * f * tt + ph)
            g = 0.045 / (1 + k * 0.35)
            out[a:b, 0] += w * env * g * (1 - pan); out[a:b, 1] += w * env * g * pan
    # bass
    fb = note(c[0] - 12)
    out[a:b] += (np.sin(2 * np.pi * fb * tt) * env * 0.09)[:, None]
# gentle pulse (sub-bass tick on the beat, 100 bpm) from 3.5s
beat = 60 / 100
for k in np.arange(3.5, DUR - 3, beat):
    a = int(k * SR); L = int(0.35 * SR)
    if a + L > n: break
    tt = np.arange(L) / SR
    out[a:a + L] += (np.sin(2 * np.pi * 55 * tt * (1 + 2 * np.exp(-tt * 30))) * np.exp(-tt * 9) * 0.12)[:, None]
    if int(round((k - 3.5) / beat)) % 2 == 1:  # soft hat
        hh = rng.normal(0, 1, int(0.06 * SR)); hh = np.diff(hh, prepend=0) * np.exp(-np.arange(len(hh)) / SR * 70) * 0.025
        out[a:a + len(hh)] += hh[:, None]
# swells into each scene change
for s in [3.3, 9.8, 14.8, 20.3, 26.3, 31.5, 38.5, 42.5]:
    L = int(0.9 * SR); a = int(s * SR) - L // 2
    nz = rng.normal(0, 1, L); nz = np.convolve(nz, np.ones(24) / 24, "same")
    e = np.sin(np.linspace(0, np.pi, L)) ** 2
    out[a:a + L, 0] += nz * e * 0.05; out[a:a + L, 1] += np.roll(nz, 200) * e * 0.05
# master: fade in/out, soft limit
fade = np.minimum(1, t / 1.0) * np.minimum(1, (DUR - t) / 2.5)
out *= fade[:, None]
out = np.tanh(out * 1.6) / 1.6
out /= max(1e-6, np.abs(out).max()) / 0.7
pcm = (out * 32767).astype(np.int16)
with wave.open("music.wav", "wb") as w:
    w.setnchannels(2); w.setsampwidth(2); w.setframerate(SR); w.writeframes(pcm.tobytes())
print("ok")
