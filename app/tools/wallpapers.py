"""Renders Mirage's original 4K wallpapers (3840x2160) with numpy.

Usage: python3 tools/wallpapers.py [out_dir] [thumb_dir]
Everything is drawn from math here: no photos, no stock, no outside assets.
Colors follow hani-alnaber.com: near-black, navy, gold and ivory.
"""
import json
import math
import sys
from pathlib import Path

import numpy as np
from PIL import Image

W, H = 3840, 2160
rng = np.random.default_rng(7)


def hexc(h):
    h = h.lstrip('#')
    return np.array([int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)], np.float32)


BG, NAVY, NAVY2, NAVY3 = hexc('04060B'), hexc('0A1122'), hexc('0E1830'), hexc('16244A')
GOLD, GOLD2, AMBER, IVORY, BLUE = hexc('F2C46B'), hexc('FFE2A6'), hexc('B9772E'), hexc('F4EFE6'), hexc('7FA8F2')

yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)


def blur1(a, r, axis):
    if r < 1:
        return a
    r = int(r)
    pad = [(0, 0)] * a.ndim
    pad[axis] = (r + 1, r)
    c = np.cumsum(np.pad(a, pad, mode='edge'), axis=axis, dtype=np.float64)
    n = a.shape[axis]
    hi = np.take(c, np.arange(2 * r + 1, 2 * r + 1 + n), axis=axis)
    lo = np.take(c, np.arange(0, n), axis=axis)
    return ((hi - lo) / (2 * r + 1)).astype(np.float32)


def blur(a, sigma):
    """Approximate gaussian: three box passes each way."""
    r = max(1, int(round(sigma * 0.9)))
    for _ in range(3):
        a = blur1(a, r, 0)
        a = blur1(a, r, 1)
    return a


def mix(a, b, t):
    t = np.asarray(t, np.float32)
    if t.ndim == 2:
        t = t[..., None]
    return a * (1 - t) + b * t


def smooth(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0, 1)
    return t * t * (3 - 2 * t)


def splat(layer, px, py, val):
    """Adds values at sub-pixel positions with bilinear weights."""
    px = np.asarray(px, np.float32)
    py = np.asarray(py, np.float32)
    val = np.broadcast_to(np.asarray(val, np.float32), px.shape)
    x0 = np.floor(px).astype(np.int64)
    y0 = np.floor(py).astype(np.int64)
    fx, fy = px - x0, py - y0
    for dx, dy, w in ((0, 0, (1 - fx) * (1 - fy)), (1, 0, fx * (1 - fy)), (0, 1, (1 - fx) * fy), (1, 1, fx * fy)):
        xi, yi = x0 + dx, y0 + dy
        ok = (xi >= 0) & (xi < W) & (yi >= 0) & (yi < H)
        np.add.at(layer, (yi[ok], xi[ok]), (val * w)[ok])


def dots(layer, px, py, val, radius):
    """Soft round dots: splat, then a blur of about the radius."""
    tmp = np.zeros_like(layer)
    splat(tmp, px, py, val)
    return layer + blur(tmp, radius) * (radius * radius * 2.4 if radius >= 1 else 1)


def stars(n, top=1.0, seed=1):
    r = np.random.default_rng(seed)
    l = np.zeros((H, W), np.float32)
    px = r.uniform(0, W, n)
    py = r.uniform(0, H * top, n)
    v = r.power(6, n) * 1.4 + 0.1
    splat(l, px, py, v)
    return blur(l, 0.8) * 3.0 + l * 0.35


def vignette(img, k=0.55):
    d = np.sqrt(((xx - W / 2) / (W * 0.62)) ** 2 + ((yy - H / 2) / (H * 0.62)) ** 2)
    return img * (1 - k * smooth(0.55, 1.35, d))[..., None]


def finish(img, path, thumb):
    img = np.clip(img, 0, 1)
    img = img + rng.normal(0, 1.3 / 255, img.shape).astype(np.float32)  # breaks up banding
    out = Image.fromarray((np.clip(img, 0, 1) * 255 + 0.5).astype(np.uint8), 'RGB')
    out.save(path, 'JPEG', quality=90, subsampling=0, optimize=True, progressive=True)
    out.resize((640, 360), Image.LANCZOS).save(thumb, 'JPEG', quality=82, optimize=True)
    print('wrote', path)


def fbm1(x, octaves, base, seed, amp=1.0):
    r = np.random.default_rng(seed)
    y = np.zeros_like(x)
    a, f = amp, base
    for _ in range(octaves):
        p = r.uniform(0, 2 * np.pi, 3)
        y += a * (np.sin(x * f + p[0]) * 0.6 + np.sin(x * f * 1.73 + p[1]) * 0.3 + np.sin(x * f * 2.31 + p[2]) * 0.1)
        a *= 0.5
        f *= 2.03
    return y


def orbits():
    cx, cy = W * 0.36, H * 0.56
    d = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2)
    img = mix(NAVY2 * 0.85, BG, smooth(0, W * 0.7, d))
    img = img + stars(1700, seed=9)[..., None] * IVORY * 0.45
    rot = math.radians(-11)
    tilt = 0.34
    line = np.zeros((H, W), np.float32)
    trail = np.zeros((H, W), np.float32)
    bodies = []
    radii = [260, 430, 640, 880, 1180, 1540, 1980, 2500]
    rr = np.random.default_rng(21)
    for i, a in enumerate(radii):
        t = np.linspace(0, 2 * np.pi, int(a * 7))
        ox, oy = a * np.cos(t), a * tilt * np.sin(t)
        px = cx + ox * math.cos(rot) - oy * math.sin(rot)
        py = cy + ox * math.sin(rot) + oy * math.cos(rot)
        behind = np.sin(t) < 0
        splat(line, px, py, np.where(behind, 0.10, 0.22))
        ang = rr.uniform(0, 2 * np.pi)
        tt = (t - ang) % (2 * np.pi)
        tail = np.clip(1 - tt / 1.3, 0, 1) ** 2 * (tt > 0)
        splat(trail, px, py, tail * 0.9)
        bx = cx + a * math.cos(ang) * math.cos(rot) - a * tilt * math.sin(ang) * math.sin(rot)
        by = cy + a * math.cos(ang) * math.sin(rot) + a * tilt * math.sin(ang) * math.cos(rot)
        size = [7, 10, 14, 9, 26, 20, 12, 16][i]
        bodies.append((bx, by, size, i))
    img = img + IVORY * (blur(line, 0.8) * 1.6 + blur(line, 5) * 1.2)[..., None] * 0.38
    img = img + GOLD * (blur(trail, 1.0) * 2.0 + blur(trail, 9) * 4)[..., None] * 0.55

    # The star.
    img = img + GOLD2 * (np.exp(-d / 30) * 1.4)[..., None]
    img = img + GOLD * (np.exp(-d / 180) * 0.55 + np.exp(-d / 700) * 0.22)[..., None]
    img = img + AMBER * (np.exp(-d / 1600) * 0.07)[..., None]

    for bx, by, s, i in bodies:
        dx, dy = xx - bx, yy - by
        r = np.sqrt(dx * dx + dy * dy)
        m = smooth(s + 1.2, s - 1.2, r)
        if m.max() <= 0:
            continue
        lx, ly = cx - bx, cy - by
        ln = math.hypot(lx, ly) or 1
        nz = np.sqrt(np.clip(1 - (r / s) ** 2, 0, 1))
        shade = np.clip((dx * lx + dy * ly) / (s * ln) * 0.9 + nz * 0.35, 0, 1)
        base = [IVORY, BLUE, GOLD, IVORY, mix(GOLD, AMBER, 0.5), BLUE, IVORY, GOLD][i]
        col = mix(NAVY * 0.6, base, shade ** 1.2)
        img = mix(img, col, m)
        img = img + base * (np.exp(-np.clip(r - s, 0, None) / (s * 0.8)) * 0.16 * (r > s))[..., None]
        if i == 4:  # a ringed one
            q = ((dx * math.cos(-0.35) - dy * math.sin(-0.35)) / (s * 2.3)) ** 2 + ((dx * math.sin(-0.35) + dy * math.cos(-0.35)) / (s * 0.55)) ** 2
            ring = np.exp(-((q - 1) / 0.06) ** 2) * ((r > s) | (dy > 0))
            img = img + GOLD2 * (ring * 0.5)[..., None]
    return vignette(img, 0.4)


# -------------------------------------------------- dot-matrix wallpapers
def _dm(name):
    import dotmatrix
    img = {'globe': dotmatrix.globe, 'dunes': dotmatrix.dunes, 'first-light': dotmatrix.first_light}[name](W, H)
    return np.asarray(img, np.float32) / 255.0


def connected_world():
    return _dm('globe')


def dune_sea():
    return _dm('dunes')


def first_light():
    return _dm('first-light')


WALLPAPERS = [
    ('connected-world', 'Connected World', 'Every line is someone reaching out. Answer the ones that matter.', connected_world),
    ('first-light', 'First Light', 'The day is quiet before it is yours. Begin with one thing.', first_light),
    ('dune-sea', 'Dune Sea', 'Wind moves mountains a grain at a time. So does steady work.', dune_sea),
    ('orbits', 'Orbits', 'Everything you care about keeps its own orbit. Give each its time.', orbits),
]

if __name__ == '__main__':
    out = Path(sys.argv[1] if len(sys.argv) > 1 else 'src-tauri/wallpapers')
    thumbs = Path(sys.argv[2] if len(sys.argv) > 2 else 'ui/wallpapers')
    only = sys.argv[3:] if len(sys.argv) > 3 else None
    out.mkdir(parents=True, exist_ok=True)
    thumbs.mkdir(parents=True, exist_ok=True)
    meta = []
    for wid, name, caption, fn in WALLPAPERS:
        meta.append({'id': wid, 'name': name, 'caption': caption})
        if only and wid not in only:
            continue
        finish(fn(), out / f'{wid}.jpg', thumbs / f'{wid}.jpg')
    (out / 'wallpapers.json').write_text(json.dumps(meta, indent=2) + '\n')
