"""Procedural 4K wallpapers for Mirage, drawn as square dot matrices in the hani-alnaber.com palette.

python3 wallpaper.py dunes out/dunes.png [width height]
python3 wallpaper.py globe out/globe.png [width height]
"""
import sys, math
import numpy as np
from PIL import Image, ImageFilter

BG = np.array([4, 6, 11], float)
NAVY = np.array([10, 17, 34], float)
NAVY2 = np.array([14, 24, 48], float)
GOLD = np.array([242, 196, 107], float)
IVORY = np.array([244, 239, 230], float)
EMBER = np.array([226, 128, 62], float)

def hash2(a, b):
    v = np.sin(a * 127.1 + b * 311.7) * 43758.5453
    return v - np.floor(v)

def dot_layer(intensity, color, cell, W, H, min_frac=0.0, max_frac=0.62, square=True):
    """Turn a per-cell intensity grid (rows, cols) into square dots on a W x H canvas.
    Returns (alpha HxW, rgb HxWx3)."""
    rows, cols = intensity.shape
    yy, xx = np.mgrid[0:H, 0:W]
    ci = np.clip(xx // cell, 0, cols - 1); ri = np.clip(yy // cell, 0, rows - 1)
    I = intensity[ri, ci]
    s = (min_frac + (max_frac - min_frac) * np.sqrt(np.clip(I, 0, 1))) * cell
    u = (xx % cell) - cell / 2 + 0.5; v = (yy % cell) - cell / 2 + 0.5
    if square:
        d = np.maximum(np.abs(u), np.abs(v))
    else:
        d = np.hypot(u, v)
    a = np.clip(s / 2 - d + 0.5, 0, 1) * (I > 0.02)
    col = color[ri, ci] if color.ndim == 3 else np.broadcast_to(color, (H, W, 3))
    return a, col

def mix(a, b, t):
    t = np.asarray(t)[..., None] if np.ndim(t) else t
    return a + (b - a) * t

def sky(W, H, horizon, glow_x=0.5, glow=1.0):
    y = np.linspace(0, 1, H)[:, None]; x = np.linspace(0, 1, W)[None, :]
    t = np.clip(y / horizon, 0, 1) ** 2.2
    img = mix(BG, NAVY, t)[..., :] * np.ones((1, W, 1))
    g = np.exp(-((x - glow_x) ** 2) / 0.05 - ((y - horizon) ** 2) / 0.004) * 0.55 * glow
    g2 = np.exp(-((x - glow_x) ** 2) / 0.4 - ((y - horizon) ** 2) / 0.03) * 0.25 * glow
    img = img + (GOLD * 0.22 + EMBER * 0.1) * (g + g2)[..., None]
    return img

def dune_height(X, Z):
    h = 0.0
    for amp, f, ang, ph, wob in [(1.0, 0.075, 0.35, 0.1, 1.2), (0.42, 0.16, 0.55, 0.7, 0.8), (0.12, 0.45, 0.2, 0.3, 0.5)]:
        s = X * math.cos(ang) + Z * math.sin(ang)
        warp = wob * np.sin(0.05 * (X * math.sin(ang) - Z * math.cos(ang)) + ph * 3) + 0.4 * np.sin(0.13 * X + 0.07 * Z + ph)
        p = s * f + warp + ph
        p = p - np.floor(p)
        h = h + amp * np.where(p < 0.78, (0.5 - 0.5 * np.cos(np.pi * p / 0.78)), (0.5 + 0.5 * np.cos(np.pi * (p - 0.78) / 0.22)) ** 1.5)
    return h * 1.5

def dunes(W, H):
    cell = max(6, W // 420)
    cols, rows = W // cell + 1, H // cell + 1
    horizon = 0.50
    hz = horizon * rows
    cam_h, scale = 7.0, rows * 1.35
    shade = np.zeros((rows, cols)); depth = np.full((rows, cols), np.inf)
    ybuf = np.full(cols, rows, float)
    xs = (np.arange(cols) - cols / 2) / cols * 1.5
    z = 5.0
    r = np.arange(rows)[:, None]
    L = np.array([0.85, 0.42, 0.32]); L /= np.linalg.norm(L)
    while z < 260:
        X = xs * z + 12; h = dune_height(X, z)
        e = 0.05
        hx = (dune_height(X + e, z) - h) / e; hz_ = (dune_height(X, z + e) - h) / e
        n = np.stack([-hx, np.ones_like(hx), -hz_]); n /= np.linalg.norm(n, axis=0)
        lam = np.clip((n * L[:, None]).sum(0), 0, 1)
        fog = math.exp(-z / 120)
        crest = np.exp(-((h / 3.0 - np.maximum.reduce([h / 3.0])) ** 2))
        val = (0.02 + 1.15 * lam ** 2.4) * (0.30 + 0.70 * fog) + 0.18 * (1 - fog)
        y = hz + (cam_h - h) / z * scale
        m = (r >= y[None, :]) & (r < ybuf[None, :])
        shade = np.where(m, val[None, :], shade); depth = np.where(m, z, depth)
        ybuf = np.minimum(ybuf, y)
        z += 0.03 + z * 0.010
    ground = np.isfinite(depth)
    rr, cc = np.mgrid[0:rows, 0:cols]
    jitter = hash2(rr, cc)
    inten = np.where(ground, np.clip(shade * (0.8 + 0.35 * jitter), 0, 1), 0.0)
    star = (~ground) & (hash2(rr * 1.3, cc * 0.7) > 0.992) & (rr < hz - 3)
    inten = np.where(star, 0.08 + 0.35 * hash2(cc, rr) ** 2, inten)
    warm = np.clip(inten, 0, 1)
    col = mix(NAVY2 * 2.4, GOLD, np.clip(warm * 1.5, 0, 1))
    col = np.where((warm > 0.8)[..., None], mix(GOLD, IVORY, np.clip((warm - 0.8) * 4, 0, 1)), col)
    col = np.where(star[..., None], IVORY * 0.85, col)
    img = sky(W, H, horizon, glow_x=0.58)
    a, c = dot_layer(inten, col, cell, W, H, 0.12, 0.72)
    out = img * (1 - a[..., None]) + c * a[..., None]
    return finish(out, W, H)

def vnoise(X, Z):
    xi, zi = np.floor(X), np.floor(Z); fx, fz = X - xi, Z - zi
    fx, fz = fx * fx * (3 - 2 * fx), fz * fz * (3 - 2 * fz)
    a, b = hash2(xi, zi), hash2(xi + 1, zi); c, d = hash2(xi, zi + 1), hash2(xi + 1, zi + 1)
    return a + (b - a) * fx + (c - a) * fz + (a - b - c + d) * fx * fz

def peak_height(X, Z):
    h, amp, f = 0.0, 1.0, 0.018
    for o in range(6):
        n = 1 - np.abs(vnoise(X * f + o * 7.3, Z * f - o * 3.1) * 2 - 1)
        h = h + amp * n ** 2.2
        amp *= 0.48; f *= 2.05
    # a valley down the middle keeps the sunrise visible
    v = np.clip(np.abs(X - 12) / 70, 0, 1)
    return h * (7 + 13 * v) - 2.0

def first_light(W, H):
    cell = max(6, W // 420)
    cols, rows = W // cell + 1, H // cell + 1
    horizon = 0.55
    hz = horizon * rows
    cam_h, scale = 7.0, rows * 1.2
    shade = np.zeros((rows, cols)); depth = np.full((rows, cols), np.inf)
    ybuf = np.full(cols, rows, float)
    xs = (np.arange(cols) - cols / 2) / cols * 1.6
    sx = (np.arange(cols) / cols - 0.5)
    z = 6.0
    r = np.arange(rows)[:, None]
    L = np.array([0.0, 0.5, -0.85]); L /= np.linalg.norm(L)
    while z < 420:
        X = xs * z + 12; h = peak_height(X, z)
        e = 0.08
        hx = (peak_height(X + e, z) - h) / e; hz_ = (peak_height(X, z + e) - h) / e
        n = np.stack([-hx, np.ones_like(hx), -hz_]); n /= np.linalg.norm(n, axis=0)
        lam = np.clip((n * L[:, None]).sum(0), 0, 1)
        fog = math.exp(-z / 160)
        y = hz + (cam_h - h) / z * scale
        m = (r >= y[None, :]) & (r < ybuf[None, :])
        base = (0.04 + 0.30 * lam ** 1.5) * (0.35 + 0.65 * fog) + 0.07 * (1 - fog)
        shade = np.where(m, base[None, :], shade); depth = np.where(m, z, depth)
        ybuf = np.minimum(ybuf, y)
        z += 0.03 + z * 0.008
    ground = np.isfinite(depth)
    # Ridgelines: where the row above is much farther away (or sky), the crest
    # catches the sunrise behind it.
    edge = np.zeros((rows, cols))
    edge[1:] = (depth[:-1] > depth[1:] * 1.15) & ground[1:]
    rim = edge.copy()
    rim[1:] += edge[:-1] * 0.45; rim[2:] += edge[:-2] * 0.18
    dfog = np.exp(-np.where(ground, depth, 400) / 160)
    sunward = np.exp(-(((np.arange(cols) / cols - 0.5) * 2.6) ** 2))[None, :]
    shade = shade + np.clip(rim, 0, 1) * (0.45 + 0.55 * sunward) * (0.45 + 0.55 * (1 - dfog))
    rr, cc = np.mgrid[0:rows, 0:cols]
    jitter = hash2(rr, cc)
    inten = np.where(ground, np.clip(shade * (0.82 + 0.3 * jitter), 0, 1), 0.0)
    star = (~ground) & (hash2(rr * 1.3, cc * 0.7) > 0.994) & (rr < hz - 14)
    inten = np.where(star, 0.05 + 0.28 * hash2(cc, rr) ** 2, inten)
    col = mix(NAVY2 * 2.4, GOLD, np.clip(inten * 1.5, 0, 1))
    col = np.where((inten > 0.8)[..., None], mix(GOLD, IVORY, np.clip((inten - 0.8) * 4, 0, 1)), col)
    col = np.where(star[..., None], IVORY * 0.85, col)
    img = sky(W, H, horizon, glow_x=0.5, glow=1.5)
    y_, x_ = np.mgrid[0:H, 0:W]
    sy = horizon - 0.035
    d2 = ((x_ / W - 0.5) * W / H) ** 2 + (y_ / H - sy) ** 2
    sun = np.clip((0.022 - np.sqrt(d2)) * 400, 0, 1) + np.exp(-d2 / 0.0009) * 0.5 + np.exp(-d2 / 0.012) * 0.25
    img = img + (mix(GOLD, IVORY, 0.6))[None, None, :] * sun[..., None]
    img = np.where(ground.repeat(cell, 0).repeat(cell, 1)[:H, :W, None], img * 0.35 + NAVY * 0.4, img)
    a, c = dot_layer(inten, col, cell, W, H, 0.12, 0.72)
    out = img * (1 - a[..., None]) + c * a[..., None]
    return finish(out, W, H)

CITIES = {"Amman": (31.95, 35.93), "Dubai": (25.2, 55.27), "Riyadh": (24.7, 46.7), "Cairo": (30.04, 31.24),
          "Istanbul": (41.0, 28.97), "London": (51.5, -0.12), "Paris": (48.85, 2.35), "Dublin": (53.35, -6.26),
          "Moscow": (55.75, 37.6), "Mumbai": (19.07, 72.88), "Singapore": (1.35, 103.8), "Beijing": (39.9, 116.4),
          "Nairobi": (-1.29, 36.82), "Lagos": (6.5, 3.4), "Johannesburg": (-26.2, 28.0), "Doha": (25.29, 51.53),
          "New York": (40.7, -74.0), "Houston": (29.76, -95.37), "Tokyo": (35.68, 139.7), "Karachi": (24.86, 67.0)}
ROUTES = [("Amman", "Dubai"), ("Amman", "London"), ("Dubai", "Mumbai"), ("Dubai", "Singapore"), ("Dubai", "London"),
          ("Cairo", "Riyadh"), ("Istanbul", "Moscow"), ("London", "New York"), ("Paris", "Lagos"), ("Nairobi", "Dubai"),
          ("Johannesburg", "Nairobi"), ("Doha", "Beijing"), ("Riyadh", "Karachi"), ("Amman", "Houston"), ("Dublin", "Istanbul"),
          ("Cairo", "Lagos"), ("Mumbai", "Singapore"), ("Moscow", "Beijing"), ("London", "Johannesburg"), ("Dubai", "Tokyo")]

def land_mask(w=2048, h=1024):
    import json
    from PIL import ImageDraw
    m = Image.new("L", (w, h), 0); d = ImageDraw.Draw(m)
    path = __file__.rsplit("/", 1)[0] + "/data/ne_110m_land.geojson" if "/" in __file__ else "data/ne_110m_land.geojson"
    for f in json.load(open(path))["features"]:
        g = f["geometry"]; polys = g["coordinates"] if g["type"] == "MultiPolygon" else [g["coordinates"]]
        for poly in polys:
            for k, ring in enumerate(poly):
                pts = [((lo + 180) / 360 * w, (90 - la) / 180 * h) for lo, la in ring]
                d.polygon(pts, fill=0 if k else 255)
    return np.asarray(m) > 127

def globe(W, H):
    cell = max(5, W // 480)
    cols, rows = W // cell + 1, H // cell + 1
    cx, cy, R = cols * 0.66, rows * 0.58, rows * 0.46
    lon0, lat0 = math.radians(38), math.radians(24)
    rr, cc = np.mgrid[0:rows, 0:cols].astype(float)
    dx, dy = (cc + 0.5 - cx) / R, (cy - rr - 0.5) / R
    rho2 = dx ** 2 + dy ** 2; on = rho2 < 1
    dz = np.sqrt(np.clip(1 - rho2, 0, 1))
    lat = np.arcsin(np.clip(dy * math.cos(lat0) + dz * math.sin(lat0), -1, 1))
    lon = lon0 + np.arctan2(dx, dz * math.cos(lat0) - dy * math.sin(lat0))
    mask = land_mask()
    mi = np.clip(((90 - np.degrees(lat)) / 180 * mask.shape[0]).astype(int), 0, mask.shape[0] - 1)
    mj = (((np.degrees(lon) + 180) % 360) / 360 * mask.shape[1]).astype(int) % mask.shape[1]
    land = mask[mi, mj] & on
    light = np.clip(dx * -0.35 + dy * 0.30 + dz * 0.88, 0, 1)
    edge = np.clip((1 - np.sqrt(rho2)) * 6, 0, 1)
    inten = np.where(land, (0.16 + 0.62 * light ** 2.0) * (0.4 + 0.6 * edge), np.where(on, 0.06 + 0.05 * light, 0.0))
    inten *= 0.82 + 0.3 * hash2(rr, cc)
    col = mix(NAVY2 * 2.1, GOLD, np.clip(inten * 1.35, 0, 1))
    col = np.where((inten > 0.85)[..., None], mix(GOLD, IVORY, np.clip((inten - 0.85) * 4, 0, 1)), col)
    img = sky(W, H, 0.98, glow_x=0.66, glow=0.2)
    y, x = np.mgrid[0:H, 0:W]
    pr = np.hypot((x - cx * cell) / (R * cell), (y - cy * cell) / (R * cell))
    halo = np.exp(-((pr - 1.0) ** 2) / 0.0008) * 0.5 + np.exp(-((pr - 1.0) ** 2) / 0.012) * 0.28
    img += (GOLD * 0.32)[None, None, :] * (halo * (pr > 0.985))[..., None]
    a, c = dot_layer(inten, col, cell, W, H, 0.0, 0.7)
    out = img * (1 - a[..., None]) + c * a[..., None]
    from PIL import ImageDraw
    arc = Image.new("L", (W, H), 0); dot = Image.new("L", (W, H), 0)
    d = ImageDraw.Draw(arc); dd = ImageDraw.Draw(dot)
    def vec(la, lo):
        la, lo = math.radians(la), math.radians(lo)
        return np.array([math.cos(la) * math.sin(lo), math.sin(la), math.cos(la) * math.cos(lo)])
    def to_view(p):
        x0, y0, z0 = p  # world: x east at lon 90, y north, z at lon 0
        # rotate by -lon0 about y, then by lat0 about x
        x1 = x0 * math.cos(lon0) - z0 * math.sin(lon0); z1 = x0 * math.sin(lon0) + z0 * math.cos(lon0)
        y2 = y0 * math.cos(lat0) - z1 * math.sin(lat0); z2 = y0 * math.sin(lat0) + z1 * math.cos(lat0)
        return x1, y2, z2
    def scr(v):
        return ((cx + v[0] * R) * cell, (cy - v[1] * R) * cell)
    for a_, b_ in ROUTES:
        p, q = vec(*CITIES[a_]), vec(*CITIES[b_])
        om = math.acos(np.clip(p @ q, -1, 1))
        pts = []
        for t in np.linspace(0, 1, 120):
            v = (math.sin((1 - t) * om) * p + math.sin(t * om) * q) / math.sin(om)
            v = v * (1 + 0.07 * om * math.sin(math.pi * t))
            vv = to_view(v)
            visible = vv[2] > 0.12
            pts.append((scr(vv), visible))
        seg = []
        for (pt, vis) in pts:
            if vis: seg.append(pt)
            elif len(seg) > 1: d.line(seg, fill=170, width=max(2, W // 1500)); seg = []
            else: seg = []
        if len(seg) > 1: d.line(seg, fill=170, width=max(2, W // 1500))
    for name, (la, lo) in CITIES.items():
        vv = to_view(vec(la, lo))
        if vv[2] > 0.05:
            px, py = scr(vv); rad = W / 700
            dd.ellipse([px - rad, py - rad, px + rad, py + rad], fill=255)
    glow = np.asarray(arc.filter(ImageFilter.GaussianBlur(W / 450)), float) / 255
    sharp = np.asarray(arc, float) / 255
    dg = np.asarray(dot.filter(ImageFilter.GaussianBlur(W / 300)), float) / 255
    ds = np.asarray(dot, float) / 255
    out += GOLD[None, None, :] * (glow * 1.1 + dg * 1.6)[..., None] + IVORY[None, None, :] * (sharp * 0.5 + ds)[..., None]
    return finish(out, W, H)

def finish(out, W, H):
    y, x = np.mgrid[0:H, 0:W]
    vig = 1 - 0.38 * (((x / W - 0.5) ** 2 + (y / H - 0.5) ** 2) * 2.2)
    out = out * vig[..., None]
    grain = (hash2(x * 0.37, y * 0.91) - 0.5) * 3.0
    out = np.clip(out + grain[..., None], 0, 255)
    return Image.fromarray(out.astype(np.uint8))

if __name__ == "__main__":
    kind, path = sys.argv[1], sys.argv[2]
    W, H = (int(sys.argv[3]), int(sys.argv[4])) if len(sys.argv) > 4 else (3840, 2160)
    {"dunes": dunes, "globe": globe, "first-light": first_light}[kind](W, H).save(path)
    print("saved", path)
