#!/usr/bin/env python3
"""Mirage promo video. Pure PIL + numpy frames piped into ffmpeg.
Usage: python3 render.py WIDTH HEIGHT out.mp4 [--preview t1,t2,...]"""
import math, os, sys, subprocess
from functools import lru_cache
from multiprocessing import Pool
import numpy as np
from PIL import Image, ImageDraw, ImageFont, ImageFilter, ImageChops

FPS = 30
DUR = 48.0
HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
FD = "/usr/share/fonts/opentype/inter/"
FONTS = {"r": FD + "Inter-Regular.otf", "m": FD + "Inter-Medium.otf", "s": FD + "Inter-SemiBold.otf", "b": FD + "Inter-Bold.otf",
         "dB": FD + "InterDisplay-Bold.otf", "dS": FD + "InterDisplay-SemiBold.otf", "dX": FD + "InterDisplay-ExtraBold.otf",
         "mono": "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf"}

W, H = 1920, 1080
U = 1.0  # unit scale

@lru_cache(None)
def font(kind, size):
    return ImageFont.truetype(FONTS[kind], max(6, int(round(size))))

# ---------- easing ----------
def clamp(x, a=0.0, b=1.0): return a if x < a else b if x > b else x
def prog(t, a, b): return clamp((t - a) / (b - a)) if b > a else float(t >= a)
def eo(x): x = clamp(x); return 1 - (1 - x) ** 3
def eio(x): x = clamp(x); return 4 * x ** 3 if x < .5 else 1 - (-2 * x + 2) ** 3 / 2
def back(x, s=1.5): x = clamp(x); x -= 1; return x * x * ((s + 1) * x + s) + 1
def lerp(a, b, x): return a + (b - a) * x
def mix(c1, c2, x): return tuple(int(lerp(a, b, x)) for a, b in zip(c1, c2))

ACC = (124, 140, 255); VIO = (192, 132, 252); TEAL = (94, 234, 212); ROSE = (255, 107, 154); AMB = (255, 181, 71); GRN = (74, 222, 128); SLATE = (124, 131, 152)
TXT = (244, 245, 250); SUB = (163, 167, 188); FAINT = (110, 114, 135)
TINTS = {"blue": ((91, 140, 255), (58, 91, 217)), "violet": ((157, 124, 255), (106, 76, 224)), "teal": ((52, 211, 192), (28, 143, 138)),
         "amber": ((255, 181, 71), (217, 130, 43)), "rose": ((255, 107, 154), (201, 59, 110)), "green": ((74, 222, 128), (31, 157, 87)),
         "slate": ((124, 131, 152), (74, 80, 99))}

# ---------- primitives ----------
@lru_cache(512)
def rr_mask(w, h, r):
    w, h = max(1, int(w)), max(1, int(h)); r = max(0, min(int(r), w // 2, h // 2))
    k = 3
    m = Image.new("L", (w * k, h * k), 0)
    ImageDraw.Draw(m).rounded_rectangle((0, 0, w * k - 1, h * k - 1), radius=r * k, fill=255)
    return m.resize((w, h), Image.LANCZOS)

@lru_cache(256)
def rr_ring(w, h, r, t=1):
    outer = rr_mask(w, h, r)
    inner = Image.new("L", (int(w), int(h)), 0)
    inner.paste(rr_mask(int(w) - 2 * t, int(h) - 2 * t, max(1, r - t)), (t, t))
    return ImageChops.subtract(outer, inner)

def grad_img(w, h, c1, c2, angle=45):
    w, h = int(w), int(h)
    a = math.radians(angle)
    xs = np.linspace(0, 1, w)[None, :]; ys = np.linspace(0, 1, h)[:, None]
    t = (xs * math.cos(a) + ys * math.sin(a)); t = (t - t.min()) / max(1e-6, (t.max() - t.min()))
    arr = np.zeros((h, w, 3), np.float32)
    for i in range(3): arr[..., i] = c1[i] + (c2[i] - c1[i]) * t
    return Image.fromarray(arr.astype(np.uint8), "RGB")

@lru_cache(64)
def grad3(w, h, angle=35):
    w, h = int(w), int(h)
    a = math.radians(angle)
    xs = np.linspace(0, 1, w)[None, :]; ys = np.linspace(0, 1, h)[:, None]
    t = (xs * math.cos(a) + ys * math.sin(a)); t = (t - t.min()) / max(1e-6, (t.max() - t.min()))
    stops = [(0, (142, 162, 255)), (0.55, (192, 132, 252)), (1, (94, 234, 212))]
    arr = np.zeros((h, w, 3), np.float32)
    for (p0, c0), (p1, c1) in zip(stops, stops[1:]):
        m = (t >= p0) & (t <= p1); u = (t - p0) / (p1 - p0)
        for i in range(3): arr[..., i] = np.where(m, c0[i] + (c1[i] - c0[i]) * u, arr[..., i])
    return Image.fromarray(arr.astype(np.uint8), "RGB")

def paste_rr(layer, xy, w, h, r, fill, alpha=255, angle=45):
    """fill: rgb tuple, (c1,c2) gradient, or 'acc' for the brand gradient."""
    w, h = int(w), int(h)
    if w <= 2 or h <= 2: return
    if fill == "acc": src = grad3(w, h).convert("RGBA")
    elif isinstance(fill[0], tuple): src = grad_img(w, h, fill[0], fill[1], angle).convert("RGBA")
    else: src = Image.new("RGBA", (w, h), fill + (255,))
    m = rr_mask(w, h, r)
    if alpha < 255: m = m.point(lambda v: v * alpha // 255)
    src.putalpha(m)
    layer.alpha_composite(src, (int(xy[0]), int(xy[1])))

def ring(layer, xy, w, h, r, color, alpha=60, t=1):
    if int(w) <= 2 * t + 2 or int(h) <= 2 * t + 2: return
    m = rr_ring(int(w), int(h), int(r), t).point(lambda v: v * alpha // 255)
    src = Image.new("RGBA", (int(w), int(h)), color + (0,)); src.putalpha(m)
    layer.alpha_composite(src, (int(xy[0]), int(xy[1])))

def text(d, xy, s, f, fill, anchor="la", alpha=255):
    d.text((int(xy[0]), int(xy[1])), s, font=f, fill=fill + (alpha,), anchor=anchor)

def shadow(layer, box, r, alpha=110, blur=40, dy=24):
    x0, y0, x1, y1 = [int(v) for v in box]
    pad = blur * 2
    w, h = x1 - x0, y1 - y0
    m = Image.new("L", (w + pad * 2, h + pad * 2), 0)
    m.paste(rr_mask(w, h, r).point(lambda v: v * alpha // 255), (pad, pad))
    m = m.resize((m.width // 4, m.height // 4)).filter(ImageFilter.GaussianBlur(blur / 4)).resize((w + pad * 2, h + pad * 2), Image.BILINEAR)
    src = Image.new("RGBA", m.size, (2, 2, 8, 0)); src.putalpha(m)
    layer.alpha_composite(src, (x0 - pad, y0 - pad + dy), )

def glass(layer, bg, box, r, tint=(16, 17, 27), strength=0.74, sheen=True, border=True):
    """Frosted dark glass: blurred background + tint + top sheen + hairline."""
    x0, y0, x1, y1 = [int(v) for v in box]
    w, h = x1 - x0, y1 - y0
    if w < 4 or h < 4: return
    cx0, cy0, cx1, cy1 = max(0, x0), max(0, y0), min(bg.width, x1), min(bg.height, y1)
    crop = Image.new("RGB", (w, h), tint)
    if cx1 > cx0 and cy1 > cy0:
        part = bg.crop((cx0, cy0, cx1, cy1)).convert("RGB")
        crop.paste(part, (cx0 - x0, cy0 - y0))
    small = crop.resize((max(1, w // 8), max(1, h // 8)), Image.BILINEAR).filter(ImageFilter.GaussianBlur(3))
    blurred = small.resize((w, h), Image.BICUBIC)
    panel = Image.blend(blurred, Image.new("RGB", (w, h), tint), strength).convert("RGBA")
    if sheen:
        sh = sheen_img(w, h)
        panel.alpha_composite(sh)
    panel.putalpha(rr_mask(w, h, r))
    layer.alpha_composite(panel, (x0, y0))
    if border:
        ring(layer, (x0, y0), w, h, r, (255, 255, 255), 46)

@lru_cache(64)
def sheen_img(w, h):
    ys = np.linspace(0, 1, h)[:, None]; xs = np.linspace(0, 1, w)[None, :]
    a = np.clip(0.10 * (1 - ys * 2.2) + 0.04 * (1 - xs), 0, 1) * 255
    arr = np.zeros((h, w, 4), np.uint8); arr[..., :3] = 255; arr[..., 3] = a.astype(np.uint8)
    return Image.fromarray(arr, "RGBA")

def opacity(layer, a):
    if a >= 0.999: return layer
    r, g, b, al = layer.split()
    al = al.point(lambda v: int(v * a))
    return Image.merge("RGBA", (r, g, b, al))

def transform(layer, scale=1.0, dx=0, dy=0, blur=0, cx=None, cy=None):
    if scale == 1.0 and dx == 0 and dy == 0 and blur == 0: return layer
    cx = W / 2 if cx is None else cx; cy = H / 2 if cy is None else cy
    if scale != 1.0:
        nw, nh = int(W * scale), int(H * scale)
        sc = layer.resize((nw, nh), Image.BICUBIC)
        out = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        out.alpha_composite(sc, (int(cx - cx * scale + dx), int(cy - cy * scale + dy))) if (int(cx - cx * scale + dx) >= 0 and int(cy - cy * scale + dy) >= 0) else out.paste(sc, (int(cx - cx * scale + dx), int(cy - cy * scale + dy)), sc)
        layer = out
    elif dx or dy:
        out = Image.new("RGBA", (W, H), (0, 0, 0, 0)); out.paste(layer, (int(dx), int(dy)), layer); layer = out
    if blur > 0.3: layer = layer.filter(ImageFilter.GaussianBlur(blur))
    return layer

# ---------- glyphs (simple line icons) ----------
def glyph(d, name, cx, cy, s, col=(255, 255, 255), a=255, wd=None):
    c = col + (a,); wd = wd or max(2, int(s * 0.11)); h = s / 2
    L = lambda pts: d.line([(cx + x * h, cy + y * h) for x, y in pts], fill=c, width=wd, joint="curve")
    E = lambda x0, y0, x1, y1: d.ellipse((cx + x0 * h, cy + y0 * h, cx + x1 * h, cy + y1 * h), outline=c, width=wd)
    R = lambda x0, y0, x1, y1, r=0.18: d.rounded_rectangle((cx + x0 * h, cy + y0 * h, cx + x1 * h, cy + y1 * h), radius=int(r * h), outline=c, width=wd)
    if name == "search": E(-.75, -.75, .35, .35); L([(.25, .25), (.8, .8)])
    elif name == "window": R(-.8, -.65, .8, .65); L([(-.8, -.3), (.8, -.3)])
    elif name == "file": L([(-.55, -.8), (.25, -.8), (.6, -.45), (.6, .8), (-.55, .8), (-.55, -.8)]); L([(.2, -.8), (.2, -.4), (.6, -.4)])
    elif name == "clip": R(-.55, -.6, .55, .85); R(-.28, -.85, .28, -.5, .1)
    elif name == "snip": L([(-.35, -.75), (-.75, 0), (-.35, .75)]); L([(.35, -.75), (.75, 0), (.35, .75)])
    elif name == "note": R(-.6, -.75, .6, .75); L([(-.3, -.3), (.3, -.3)]); L([(-.3, .05), (.3, .05)]); L([(-.3, .4), (.05, .4)])
    elif name == "stack": R(-.75, -.2, .45, .75); L([(-.45, -.45), (.75, -.45), (.75, .45)]); L([(-.15, -.75), (.95, -.75)])
    elif name == "cmd": L([(-.7, -.5), (-.15, 0), (-.7, .5)]); L([(.05, .55), (.7, .55)])
    elif name == "spark":
        pts = []
        for i in range(8):
            ang = i * math.pi / 4 - math.pi / 2; rr = .85 if i % 2 == 0 else .28
            pts.append((math.cos(ang) * rr, math.sin(ang) * rr))
        d.polygon([(cx + x * h, cy + y * h) for x, y in pts], fill=c)
    elif name == "calc": R(-.6, -.8, .6, .8); L([(-.3, -.45), (.3, -.45)]); [d.ellipse((cx + x * h - wd / 2, cy + y * h - wd / 2, cx + x * h + wd / 2, cy + y * h + wd / 2), fill=c) for x in (-.3, 0, .3) for y in (.05, .45)]
    elif name == "pin": L([(0, .85), (0, .15)]); d.polygon([(cx - .5 * h, cy + .15 * h), (cx + .5 * h, cy + .15 * h), (cx + .3 * h, cy - .2 * h), (cx + .3 * h, cy - .75 * h), (cx - .3 * h, cy - .75 * h), (cx - .3 * h, cy - .2 * h)], outline=c, width=wd)
    elif name == "shield": L([(0, -.85), (.7, -.55), (.6, .25), (0, .85), (-.6, .25), (-.7, -.55), (0, -.85)])
    elif name == "bolt": d.polygon([(cx + .15 * h, cy - .9 * h), (cx - .55 * h, cy + .1 * h), (cx - .05 * h, cy + .1 * h), (cx - .2 * h, cy + .9 * h), (cx + .55 * h, cy - .15 * h), (cx + .05 * h, cy - .15 * h)], fill=c)
    elif name == "globe": E(-.8, -.8, .8, .8); E(-.35, -.8, .35, .8); L([(-.8, 0), (.8, 0)])
    elif name == "grid": [R(x, y, x + .65, y + .65, .15) for x in (-.75, .1) for y in (-.75, .1)]
    elif name == "music": L([(-.35, .55), (-.35, -.6), (.55, -.8), (.55, .35)]); d.ellipse((cx - .75 * h, cy + .3 * h, cx - .3 * h, cy + .75 * h), fill=c); d.ellipse((cx + .15 * h, cy + .1 * h, cx + .6 * h, cy + .55 * h), fill=c)
    elif name == "braces": text(d, (cx, cy), "{ }", font("b", s * .62), col, "mm", a)
    elif name == "lock": R(-.6, -.1, .6, .8); d.arc((cx - .38 * h, cy - .8 * h, cx + .38 * h, cy + .1 * h), 180, 360, fill=c, width=wd)
    elif name == "check": L([(-.6, 0), (-.15, .45), (.65, -.45)])
    elif name == "translate": text(d, (cx - .3 * h, cy - .1 * h), "A", font("b", s * .55), col, "mm", a); text(d, (cx + .35 * h, cy + .25 * h), "文", font("b", s * .45), col, "mm", a) if False else L([(.15, .3), (.75, .3)])
    elif name == "wand": L([(-.75, .75), (.35, -.35)]); glyph(d, "spark", cx + .5 * h, cy - .5 * h, s * .5, col, a)
    elif name == "reply": L([(-.2, -.6), (-.75, -.1), (-.2, .4)]); d.arc((cx - .75 * h, cy - .1 * h - .7 * h, cx + .8 * h, cy + .9 * h), 270, 360, fill=c, width=wd)
    elif name == "case": text(d, (cx, cy + s * .02), "Aa", font("b", s * .6), col, "mm", a)
    elif name == "gear":
        E(-.42, -.42, .42, .42)
        for k in range(8):
            ang = k * math.pi / 4
            L([(math.cos(ang) * .55, math.sin(ang) * .55), (math.cos(ang) * .85, math.sin(ang) * .85)])
    elif name == "touch":
        R(-.85, -.6, .85, .65, .25)
        for x in (-.35, 0, .35): d.ellipse((cx + x * h - .11 * h, cy - .05 * h - .11 * h, cx + x * h + .11 * h, cy - .05 * h + .11 * h), fill=c)
    elif name == "radial":
        d.ellipse((cx - .2 * h, cy - .2 * h, cx + .2 * h, cy + .2 * h), fill=c)
        for k in range(6):
            ang = k * math.pi / 3 - math.pi / 2
            E(math.cos(ang) * .7 - .17, math.sin(ang) * .7 - .17, math.cos(ang) * .7 + .17, math.sin(ang) * .7 + .17)
    elif name == "swipe":
        L([(-.85, 0), (.85, 0)]); L([(-.5, -.35), (-.85, 0), (-.5, .35)]); L([(.5, -.35), (.85, 0), (.5, .35)])
    elif name == "key":
        R(-.85, -.55, .85, .55, .2)
        for x in (-.45, -.05, .35):
            d.rectangle((cx + x * h, cy - .25 * h, cx + (x + .15) * h, cy - .1 * h), fill=c)
        L([(-.4, .25), (.4, .25)])
    else: E(-.5, -.5, .5, .5)

def tile(layer, x, y, s, tint, g, r=None):
    c1, c2 = TINTS[tint]
    paste_rr(layer, (x, y), s, s, r or int(s * .28), (c1, c2), 255, 45)
    d = ImageDraw.Draw(layer)
    if len(g) <= 3 and g not in ("cmd", "pin", "key"):
        text(d, (x + s / 2, y + s / 2 + 1), g, font("b", s * .42), (255, 255, 255), "mm")
    else:
        glyph(d, g, x + s / 2, y + s / 2, s * .5)

# ---------- background ----------
_BG = {}
def orb(rad, color, peak):
    key = (rad, color, peak)
    if key not in _BG:
        n = rad * 2
        ys, xs = np.mgrid[0:n, 0:n].astype(np.float32)
        dist = np.sqrt((xs - rad) ** 2 + (ys - rad) ** 2) / rad
        a = np.clip(1 - dist, 0, 1) ** 2.2 * peak * 255
        arr = np.zeros((n, n, 4), np.uint8); arr[..., :3] = color; arr[..., 3] = a.astype(np.uint8)
        _BG[key] = Image.fromarray(arr, "RGBA")
    return _BG[key]

def base_bg():
    if "base" not in _BG:
        ys = np.linspace(0, 1, H)[:, None, None]
        top = np.array([9, 10, 17], np.float32); bot = np.array([5, 5, 9], np.float32)
        arr = (top + (bot - top) * ys) * np.ones((1, W, 1), np.float32)
        # faint dot grid for texture
        g = int(34 * U)
        arr[::g, ::g] += 10
        img = Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8), "RGB").convert("RGBA")
        _BG["base"] = img
        # vignette
        yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
        dd = np.sqrt(((xx - W / 2) / (W * .7)) ** 2 + ((yy - H / 2) / (H * .75)) ** 2)
        v = (np.clip(dd - .45, 0, 1) * 200).astype(np.uint8)
        va = np.zeros((H, W, 4), np.uint8); va[..., 3] = v
        _BG["vig"] = Image.fromarray(va, "RGBA")
    return _BG["base"].copy()

def background(t, energy=1.0):
    img = base_bg()
    m = min(W, H)
    rad = int(m * 0.62)
    spots = [
        (ACC, .50, .28 + .10 * math.sin(t * .23), .30 + .12 * math.cos(t * .19)),
        (VIO, .42, .74 + .10 * math.cos(t * .17), .36 + .14 * math.sin(t * .21)),
        (TEAL, .26, .58 + .14 * math.sin(t * .13 + 1), .86 + .06 * math.cos(t * .25)),
        (ROSE, .16, .12 + .08 * math.cos(t * .15), .88 + .05 * math.sin(t * .2)),
    ]
    for col, peak, fx, fy in spots:
        o = orb(rad, col, peak * energy)
        img.alpha_composite(o, (int(fx * W - rad), int(fy * H - rad))) if 0 <= int(fx * W - rad) and 0 <= int(fy * H - rad) else img.paste(o, (int(fx * W - rad), int(fy * H - rad)), o) if False else _paste_any(img, o, int(fx * W - rad), int(fy * H - rad))
    img.alpha_composite(_BG["vig"])
    return img

def _paste_any(dst, src, x, y):
    # alpha_composite with negative offsets
    sx0, sy0 = max(0, -x), max(0, -y)
    dx0, dy0 = max(0, x), max(0, y)
    w = min(src.width - sx0, dst.width - dx0); h = min(src.height - sy0, dst.height - dy0)
    if w > 0 and h > 0:
        dst.alpha_composite(src.crop((sx0, sy0, sx0 + w, sy0 + h)), (dx0, dy0))

# ---------- launcher ----------
class Row:
    def __init__(self, title, sub="", badge="", tint="violet", g="search"):
        self.title, self.sub, self.badge, self.tint, self.g = title, sub, badge, tint, g

def launcher(layer, bg, cx, top, width, query, rows, sel=0, t=0.0, chip=None, placeholder="Search apps, files, or type ? to ask AI",
             caret=True, actions=None, act_sel=0, act_open=1.0, selection_text=None, sel_glow=0.0, rows_shown=None, row_fade=1.0, armed_status=None):
    s = U
    rh = int(56 * s); head = int(80 * s); foot = int(44 * s); pad = int(10 * s)
    selh = int(58 * s) if selection_text else 0
    n = len(rows) if rows_shown is None else rows_shown
    hgt = head + selh + (pad * 2 + rh * n if n else 0) + foot
    x0 = int(cx - width / 2); y0 = int(top)
    shadow(layer, (x0, y0, x0 + width, y0 + hgt), int(16 * s), 150, int(50 * s), int(26 * s))
    glass(layer, bg, (x0, y0, x0 + width, y0 + hgt), int(16 * s))
    d = ImageDraw.Draw(layer)
    # search row
    ts = int(38 * s)
    paste_rr(layer, (x0 + 22 * s, y0 + (head - ts) / 2), ts, ts, int(11 * s), "acc")
    glyph(d, "search" if not chip else chip[1], x0 + 22 * s + ts / 2, y0 + head / 2, ts * .46)
    qx = x0 + 22 * s + ts + 18 * s
    fq = font("r", 27 * s)
    if query:
        text(d, (qx, y0 + head / 2), query, fq, TXT, "lm")
        qw = d.textlength(query, font=fq)
    else:
        text(d, (qx, y0 + head / 2), placeholder, fq, FAINT, "lm")
        qw = 0
    if caret and (int(t * 2.2) % 2 == 0):
        d.rounded_rectangle((qx + qw + 3 * s, y0 + head / 2 - 16 * s, qx + qw + 5 * s, y0 + head / 2 + 16 * s), radius=1, fill=ACC + (255,))
    if chip:
        fc = font("s", 15 * s); cw = d.textlength(chip[0], font=fc) + 24 * s
        paste_rr(layer, (x0 + width - cw - 22 * s, y0 + head / 2 - 15 * s), cw, 30 * s, int(9 * s), (255, 255, 255), 24)
        ring(layer, (x0 + width - cw - 22 * s, y0 + head / 2 - 15 * s), cw, 30 * s, int(9 * s), (255, 255, 255), 40)
        text(d, (x0 + width - cw / 2 - 22 * s, y0 + head / 2), chip[0], fc, SUB, "mm")
    y = y0 + head
    if selection_text:
        paste_rr(layer, (x0 + 22 * s, y), width - 44 * s, selh - 12 * s, int(10 * s), (255, 255, 255), 16)
        ring(layer, (x0 + 22 * s, y), width - 44 * s, selh - 12 * s, int(10 * s), (255, 255, 255), 34)
        d = ImageDraw.Draw(layer)
        glyph(d, "snip", x0 + 44 * s, y + (selh - 12 * s) / 2, 18 * s, ACC)
        text(d, (x0 + 64 * s, y + (selh - 12 * s) / 2), selection_text, font("r", 16 * s), SUB, "lm")
        y += selh
    d.line((x0 + 1, y, x0 + width - 2, y), fill=(255, 255, 255, 28), width=1)
    y += pad
    for i, r in enumerate(rows[:n]):
        ry = y + i * rh
        fade = 1.0 if i < n - 1 or row_fade >= 1 else row_fade
        if i == sel:
            paste_rr(layer, (x0 + 10 * s, ry + 2 * s), width - 20 * s, rh - 4 * s, int(11 * s), ((124, 140, 255), (192, 132, 252)), int(70 + 60 * sel_glow), 0)
            ring(layer, (x0 + 10 * s, ry + 2 * s), width - 20 * s, rh - 4 * s, int(11 * s), (180, 170, 255), int(40 + 80 * sel_glow))
        is_ = int(40 * s)
        tile(layer, x0 + 22 * s, ry + (rh - is_) / 2, is_, r.tint, r.g)
        d = ImageDraw.Draw(layer)
        tx = x0 + 22 * s + is_ + 16 * s
        if r.sub:
            text(d, (tx, ry + rh / 2 - 10 * s), r.title, font("m", 19 * s), TXT, "lm", int(255 * fade))
            text(d, (tx, ry + rh / 2 + 12 * s), r.sub, font("r", 14.5 * s), FAINT, "lm", int(255 * fade))
        else:
            text(d, (tx, ry + rh / 2), r.title, font("m", 19 * s), TXT, "lm", int(255 * fade))
        if r.badge:
            fb = font("m", 13.5 * s); bw = d.textlength(r.badge, font=fb) + 20 * s
            paste_rr(layer, (x0 + width - bw - 22 * s, ry + rh / 2 - 13 * s), bw, 26 * s, int(7 * s), (255, 255, 255), 22)
            d = ImageDraw.Draw(layer)
            text(d, (x0 + width - bw / 2 - 22 * s, ry + rh / 2), r.badge, fb, SUB, "mm")
    # footer
    fy = y0 + hgt - foot
    d.line((x0 + 1, fy, x0 + width - 2, fy), fill=(255, 255, 255, 24), width=1)
    text(d, (x0 + 22 * s, fy + foot / 2), "Mirage", font("s", 14 * s), ACC, "lm")
    if armed_status:
        text(d, (x0 + 92 * s, fy + foot / 2), armed_status, font("r", 14 * s), ROSE, "lm")
    hint = "↵ Open    Tab Actions    Esc Close"
    text(d, (x0 + width - 22 * s, fy + foot / 2), hint, font("r", 14 * s), FAINT, "rm")
    # action panel
    if actions and act_open > 0.01:
        aw = int(340 * s); ah = int(len(actions) * 44 * s + 52 * s)
        ax = x0 + width - aw - 16 * s + (1 - eo(act_open)) * 40 * s; ay = fy - ah - 10 * s
        al = Image.new("RGBA", layer.size, (0, 0, 0, 0))
        shadow(al, (ax, ay, ax + aw, ay + ah), int(14 * s), 160, int(30 * s), int(12 * s))
        paste_rr(al, (ax, ay), aw, ah, int(14 * s), (22, 23, 34), 250)
        ring(al, (ax, ay), aw, ah, int(14 * s), (255, 255, 255), 50)
        dd = ImageDraw.Draw(al)
        text(dd, (ax + 18 * s, ay + 24 * s), "Actions", font("s", 13 * s), FAINT, "lm")
        for j, (nm, g, sc) in enumerate(actions):
            yy = ay + 44 * s + j * 44 * s
            if j == act_sel:
                paste_rr(al, (ax + 8 * s, yy), aw - 16 * s, 40 * s, int(9 * s), ((124, 140, 255), (192, 132, 252)), 80, 0)
                dd = ImageDraw.Draw(al)
            glyph(dd, g, ax + 30 * s, yy + 20 * s, 18 * s, SUB)
            text(dd, (ax + 52 * s, yy + 20 * s), nm, font("m", 16 * s), TXT, "lm")
            if sc: text(dd, (ax + aw - 20 * s, yy + 20 * s), sc, font("r", 13 * s), FAINT, "rm")
        layer.alpha_composite(opacity(al, eo(act_open)))
    return hgt

# ---------- captions ----------
def caption(layer, t, kicker, title, sub, y, appear=0.0, leave=None):
    d = ImageDraw.Draw(layer)
    a_in = eo(prog(t, appear, appear + .55))
    if a_in <= 0: return
    a = a_in * (1 - eo(prog(t, leave, leave + .4)) if leave is not None else 1)
    big = font("dB", (64 if W > H else 60) * U)
    if kicker:
        fk = font("s", 15 * U)
        kw = d.textlength(kicker, font=fk) + 28 * U
        ka = eo(prog(t, appear, appear + .4)) * a
        ky = y - 112 * U
        paste_rr(layer, (W / 2 - kw / 2, ky), kw, 32 * U, int(16 * U), (255, 255, 255), int(22 * ka))
        ring(layer, (W / 2 - kw / 2, ky), kw, 32 * U, int(16 * U), ACC, int(110 * ka))
        d = ImageDraw.Draw(layer)
        text(d, (W / 2, ky + 16 * U), kicker, fk, (190, 198, 255), "mm", int(255 * ka))
    # words rise in with stagger
    words = title.split(" ")
    widths = [d.textlength(w_ + " ", font=big) for w_ in words]
    total = sum(widths) - d.textlength(" ", font=big)
    lines = [[]]; lw = [0.0]; maxw = W * .86
    for w_, ww in zip(words, widths):
        if lw[-1] + ww > maxw and lines[-1]: lines.append([]); lw.append(0.0)
        lines[-1].append((w_, ww)); lw[-1] += ww
    k = 0
    for li, line in enumerate(lines):
        x = W / 2 - (sum(ww for _, ww in line) - d.textlength(" ", font=big)) / 2
        for w_, ww in line:
            p = eo(prog(t, appear + .06 * k, appear + .06 * k + .6))
            text(d, (x, y + li * 74 * U + (1 - p) * 28 * U), w_, big, TXT, "ls", int(255 * p * a))
            x += ww; k += 1
    if sub:
        p = eo(prog(t, appear + .25, appear + .85))
        text(d, (W / 2, y + (len(lines) - 1) * 74 * U + 44 * U + (1 - p) * 14 * U), sub, font("r", 23 * U), SUB, "mm", int(255 * p * a))
    return len(lines)

def keycap(layer, x, y, label, press=0.0, w=None):
    s = U
    f = font("s", 26 * s)
    d = ImageDraw.Draw(layer)
    w = w or int(d.textlength(label, font=f) + 56 * s)
    h = int(74 * s)
    dy = 7 * s * press
    shadow(layer, (x, y + 10 * s, x + w, y + h + 10 * s), int(16 * s), int(140 * (1 - press * .6)), int(24 * s), int(12 * s))
    paste_rr(layer, (x, y + 9 * s), w, h, int(16 * s), (12, 12, 20), 255)
    paste_rr(layer, (x, y + dy), w, h, int(16 * s), ((44, 46, 64), (26, 27, 40)), 255, 90)
    if press > 0.05:
        paste_rr(layer, (x, y + dy), w, h, int(16 * s), "acc", int(110 * press))
    ring(layer, (x, y + dy), w, h, int(16 * s), (255, 255, 255), 60)
    d = ImageDraw.Draw(layer)
    text(d, (x + w / 2, y + dy + h / 2), label, f, TXT, "mm")
    return w

def type_text(full, t, start, cps=11):
    n = int(clamp((t - start) * cps, 0, len(full)))
    return full[:n]

# ---------- scenes ----------
LOGO = None
def logo_img(size):
    global LOGO
    if LOGO is None: LOGO = Image.open(os.path.join(ROOT, "assets", "mirage-1024.png")).convert("RGBA")
    return LOGO.resize((int(size), int(size)), Image.LANCZOS)

def panel_w(): return int(min(1000 * U, W * .86))

def capy(): return int((150 if W > H else 200) * U)
def ltop(): return int((262 if W > H else 390) * U)

def scene_intro(bg, t):
    L = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    s = back(prog(t, .1, 1.1), 1.2)
    size = 220 * U * max(0.01, s)
    cy = H * .40
    glow = orb(int(260 * U), VIO, .55 * eo(prog(t, .2, 1.2)))
    _paste_any(L, glow, int(W / 2 - glow.width / 2), int(cy - glow.height / 2))
    if size > 4:
        lg = logo_img(size)
        lg = opacity(lg, eo(prog(t, .1, .6)))
        L.alpha_composite(lg, (int(W / 2 - size / 2), int(cy - size / 2)))
    d = ImageDraw.Draw(L)
    p = eo(prog(t, .7, 1.4))
    text(d, (W / 2, cy + 175 * U + (1 - p) * 30 * U), "Mirage", font("dX", 112 * U), TXT, "mm", int(255 * p))
    p2 = eo(prog(t, 1.2, 1.9))
    text(d, (W / 2, cy + 262 * U + (1 - p2) * 16 * U), "The touchpad-first launcher for Windows.", font("r", 30 * U), SUB, "mm", int(255 * p2))
    # shimmer line
    return L

HOME = [Row("Visual Studio Code", "Pinned", "App", "blue", "braces"), Row("Terminal", "Pinned", "App", "slate", "cmd"),
        Row("Spotify", "Pinned", "App", "green", "music"), Row("Switch windows", "Type w and a space", "w", "violet", "window"),
        Row("Clipboard history", "Type c and a space", "c", "violet", "clip"), Row("Ask AI", "Type ? and your question", "?", "violet", "spark")]
SEARCH_STEPS = {
    "v": [Row("Visual Studio Code", "App", "App", "blue", "braces"), Row("Vivaldi", "App", "App", "rose", "globe"), Row("Volume mixer", "Settings", "Setting", "slate", "grid"), Row("Video editor", "Store app", "App", "amber", "grid")],
    "vs": [Row("Visual Studio Code", "App", "App", "blue", "braces"), Row("Visual Studio 2022", "App", "App", "violet", "braces"), Row("Search the web for \"vs\"", "Google", "Web", "slate", "globe")],
    "vsc": [Row("Visual Studio Code", "App · opened 14 times this week", "App", "blue", "braces"), Row("vscode-settings.json", "Documents", "File", "teal", "file"),
            Row("Search the web for \"vsc\"", "Google", "Web", "slate", "globe"), Row("Ask ChatGPT about \"vsc\"", "Default AI", "AI", "violet", "spark")],
}

def scene_search(bg, t):
    L = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    caption(L, t, "SEARCH", "Alt + Space. Then just type.", "Apps, settings, files, windows, the web. Ranked by what you use.", capy(), .1)
    # keycaps press
    kc = prog(t, .3, .7); press = math.sin(prog(t, .9, 1.5) * math.pi)
    lp = eo(prog(t, 1.4, 2.0))  # launcher in
    if lp < 1:
        a = eo(kc) * (1 - eo(prog(t, 1.3, 1.7)))
        if a > 0:
            K = Image.new("RGBA", (W, H), (0, 0, 0, 0))
            w1 = 120 * U; w2 = 260 * U; gap = 70 * U
            x = W / 2 - (w1 + w2 + gap) / 2; y = H * .52
            keycap(K, x, y, "Alt", press, int(w1))
            ImageDraw.Draw(K).text((x + w1 + gap / 2, y + 37 * U), "+", font=font("s", 34 * U), fill=SUB + (255,), anchor="mm")
            keycap(K, x + w1 + gap, y, "Space", press, int(w2))
            L.alpha_composite(opacity(K, a))
    if lp > 0:
        q = type_text("vsc", t, 2.7, 6)
        rows = HOME if not q else SEARCH_STEPS[q]
        sel = 0
        if q == "vsc": sel = 1 if 4.1 < t < 4.8 else 0
        glow = math.sin(prog(t, 5.6, 6.6) * math.pi)
        P = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        launcher(P, bg, W / 2, ltop(), panel_w(), q, rows, sel, t, sel_glow=glow)
        sc = lerp(.94, 1.0, back(lp, 1.4))
        P = transform(opacity(P, lp), sc, 0, (1 - lp) * -20 * U, cy=ltop())
        L.alpha_composite(P)
    return L

def scene_calc(bg, t):
    L = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    caption(L, t, "CALCULATOR", "Math and units, right in the bar.", "Press Enter to copy the answer.", capy(), .2)
    if t < 2.4:
        q = type_text("= 1299 * 0.85", t, .5, 14)
        rows = [Row("1,104.15", "1299 * 0.85  ·  Enter to copy", "Calc", "amber", "calc")] if len(q) >= 13 else [Row("Calculator", "Type an expression", "=", "amber", "calc")]
        chip = ("Calculator", "calc")
    else:
        q = type_text("10 km to mi", t, 2.6, 14)
        rows = [Row("6.2137 mi", "10 km to mi  ·  Enter to copy", "Units", "teal", "calc")] if len(q) >= 11 else [Row("Keep typing…", "Units: length, weight, data, time, temperature", "", "slate", "calc")]
        chip = None
    launcher(L, bg, W / 2, ltop(), panel_w(), q, rows, 0, t, chip=chip, sel_glow=math.sin(prog(t, 1.6, 2.3) * math.pi) + math.sin(prog(t, 3.9, 4.6) * math.pi))
    return L

CLIPS = [Row("Meeting moved to 3:30, same link", "Copied 2 minutes ago", "Text", "violet", "clip"),
         Row("https://github.com/hanialnaber/mirage-launcher", "Copied 9 minutes ago", "Link", "blue", "globe"),
         Row('{"name":"mirage","version":"1.0.0"}', "Copied 25 minutes ago", "Text", "violet", "clip"),
         Row("Thanks so much, talk soon!", "Copied 1 hour ago", "Text", "violet", "clip"),
         Row("#7C8CFF", "Copied yesterday", "Text", "violet", "clip")]
CLIP_ACTIONS = [("Paste", "clip", "↵"), ("Copy", "clip", "Ctrl+C"), ("Ask AI about this", "spark", "Shift+↵"), ("Paste as UPPERCASE", "case", ""),
                ("Paste as JSON (pretty)", "braces", ""), ("Save as note", "note", "")]

def scene_clip(bg, t):
    L = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    caption(L, t, "CLIPBOARD", "Clipboard history that does more.", "Paste anything again, or transform it on the way in.", capy(), .2)
    q = type_text("c ", t, .4, 8)
    rows = CLIPS if q == "c " else HOME
    sel = 2 if t > 1.8 else (1 if t > 1.4 else 0)
    act = prog(t, 2.3, 2.7)
    asel = 0 if t < 3.2 else (1 if t < 3.5 else (2 if t < 3.8 else 4))
    launcher(L, bg, W / 2, ltop(), panel_w(), q, rows, sel, t, chip=("Clipboard", "clip") if q == "c " else None,
             actions=CLIP_ACTIONS, act_sel=asel, act_open=act)
    return L

SEL_ROWS = [Row("Explain", "Ask your AI to explain it simply", "AI", "violet", "spark"), Row("Summarize", "A few bullet points", "AI", "violet", "spark"),
            Row("Fix grammar and tone", "Replace the selection with a clean version", "AI", "violet", "wand"),
            Row("Translate to Arabic", "", "AI", "violet", "globe"), Row("Reply to this", "Draft a short, friendly reply", "AI", "violet", "reply"),
            Row("UPPERCASE", "Replace the selection", "Text", "teal", "case")]

def scene_select(bg, t):
    L = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    caption(L, t, "QUICK ACTIONS", "Select text anywhere. Act on it.", "Swipe up with three fingers, or press Ctrl + Alt + Space.", capy(), .2)
    # a mock editor window
    ew = panel_w(); ex = W / 2 - ew / 2; ey = ltop(); eh = int(300 * U)
    lp = eo(prog(t, 2.0, 2.5))
    ea = 1 - .55 * lp
    E = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    shadow(E, (ex, ey, ex + ew, ey + eh), int(14 * U), 120, int(40 * U), int(20 * U))
    paste_rr(E, (ex, ey), ew, eh, int(14 * U), (24, 25, 36), 250)
    ring(E, (ex, ey), ew, eh, int(14 * U), (255, 255, 255), 40)
    d = ImageDraw.Draw(E)
    for i, c in enumerate([(255, 95, 87), (254, 188, 46), (40, 200, 64)]):
        d.ellipse((ex + 22 * U + i * 22 * U, ey + 20 * U, ex + 34 * U + i * 22 * U, ey + 32 * U), fill=c + (200,))
    text(d, (ex + ew / 2, ey + 26 * U), "Message draft", font("m", 15 * U), FAINT, "mm")
    lines = ["Hi team, quick update on the launch.", "we has finish the beta and the feedback have been great,", "so lets plan the public release for next week.", "Thanks!"]
    fy = ey + 74 * U
    f = font("r", 23 * U)
    hl = eo(prog(t, .5, 1.3))
    for i, ln in enumerate(lines):
        yy = fy + i * 46 * U
        if i in (1, 2) and hl > 0:
            lw = d.textlength(ln, font=f)
            frac = clamp(hl * 2 - (i - 1))
            if frac > 0:
                paste_rr(E, (ex + 40 * U - 4 * U, yy - 6 * U), lw * frac + 8 * U, 36 * U, int(5 * U), ACC, 95)
                d = ImageDraw.Draw(E)
        text(d, (ex + 40 * U, yy), ln, f, (220, 222, 232), "la")
    L.alpha_composite(opacity(E, ea))
    # three-finger swipe up on the touchpad
    touchpad(L, bg, swipe_fingers(prog(t, 1.15, 2.05), 3, 0, -1), "Three-finger swipe up", eo(prog(t, .3, .7)) * (1 - eo(prog(t, 4.6, 5.0))))
    if lp > 0:
        P = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        sel = 2 if t > 3.4 else (1 if t > 3.0 else 0)
        launcher(P, bg, W / 2, ltop() + 30 * U, panel_w() - 60 * U, "", SEL_ROWS, sel, t, chip=("Selected text", "snip"),
                 placeholder="What should I do with the selected text?", selection_text="we has finish the beta and the feedback have been great, so lets…",
                 sel_glow=math.sin(prog(t, 4.0, 4.7) * math.pi))
        P = transform(opacity(P, lp), lerp(.95, 1, back(lp)), 0, 0, cy=ltop())
        L.alpha_composite(P)
    # fixed text toast
    tp = eo(prog(t, 4.6, 5.0))
    if tp > 0:
        d = ImageDraw.Draw(L)
        msg = "Fixed: \"We have finished the beta, and the feedback has been great.\""
    return L

APPS = [("VS Code", "blue", "braces"), ("Spotify", "green", "music"), ("Notion", "slate", "note"), ("github.com", "violet", "globe")]
def scene_ws(bg, t):
    L = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    caption(L, t, "WORKSPACES", "One command. Your whole setup.", "Open apps, sites and folders together. Save your open windows as one.", capy(), .2)
    q = type_text("ws focus", t, .4, 12)
    rows = [Row("Focus", "VS Code, Spotify, Notion, github.com", "Workspace", "teal", "stack"),
            Row("Save open windows as \"focus\"", "Snapshot what you have open right now", "New", "slate", "stack")] if len(q) >= 4 else HOME
    out = eo(prog(t, 2.2, 2.6))
    if out < 1:
        P = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        launcher(P, bg, W / 2, ltop(), panel_w(), q, rows, 0, t, chip=("Workspaces", "stack") if len(q) >= 3 else None, sel_glow=math.sin(prog(t, 1.7, 2.3) * math.pi))
        L.alpha_composite(transform(opacity(P, 1 - out), 1 - .06 * out, cy=ltop()))
    if t > 2.3:
        n = len(APPS)
        cols = n if W > H else 2
        tw = int(250 * U); th = int(190 * U); gap = int(28 * U)
        rowsn = math.ceil(n / cols)
        gx = W / 2 - (cols * tw + (cols - 1) * gap) / 2; gy = ltop() + 20 * U
        for i, (nm, tint, g) in enumerate(APPS):
            p = back(prog(t, 2.4 + i * .14, 2.9 + i * .14), 1.6)
            if p <= 0: continue
            cx_ = gx + (i % cols) * (tw + gap); cy_ = gy + (i // cols) * (th + gap)
            T = Image.new("RGBA", (W, H), (0, 0, 0, 0))
            shadow(T, (cx_, cy_, cx_ + tw, cy_ + th), int(18 * U), 120, int(30 * U), int(16 * U))
            glass(T, bg, (cx_, cy_, cx_ + tw, cy_ + th), int(18 * U))
            tile(T, cx_ + tw / 2 - 36 * U, cy_ + 36 * U, int(72 * U), tint, g)
            d = ImageDraw.Draw(T)
            text(d, (cx_ + tw / 2, cy_ + 140 * U), nm, font("s", 20 * U), TXT, "mm")
            text(d, (cx_ + tw / 2, cy_ + 166 * U), "Opening…" if t < 3.6 + i * .1 else "Ready", font("r", 14 * U), GRN if t >= 3.6 + i * .1 else FAINT, "mm")
            L.alpha_composite(transform(opacity(T, clamp(p)), lerp(.6, 1, clamp(p)) if p < 1 else 1.0, 0, (1 - clamp(p)) * 60 * U, cx=cx_ + tw / 2, cy=cy_ + th / 2) if p < 1 else T)
    return L

def scene_agents(bg, t):
    L = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    caption(L, t, "AI AGENTS", "Let your AI agents use Windows. Safely.", "A local MCP and REST bridge: token protected, off until you turn it on.", capy(), .2)
    wide = W > H
    cy = ltop() + (200 if wide else 230) * U
    cw = int((250 if wide else 220) * U); ch = int(64 * U)
    left = ["Claude Desktop", "Cursor", "Your own bot"]
    right = [("Apps", "grid"), ("Windows", "window"), ("Clipboard", "clip"), ("Files", "file")]
    lx = W * (.13 if wide else .05); rx = W * (.87 if wide else .95) - cw
    hub = int(150 * U)
    # links + flowing dots
    d = ImageDraw.Draw(L)
    lines = []
    for i, nm in enumerate(left):
        y = cy + (i - 1) * (ch + 26 * U)
        lines.append(((lx + cw, y), (W / 2 - hub / 2, cy)))
    for i, _ in enumerate(right):
        y = cy + (i - 1.5) * (ch * .82 + 20 * U)
        lines.append(((W / 2 + hub / 2, cy), (rx, y)))
    lp = eo(prog(t, .6, 1.4))
    for j, (a, b) in enumerate(lines):
        ex_ = (lerp(a[0], b[0], lp), lerp(a[1], b[1], lp))
        d.line((a, ex_), fill=(150, 160, 255, 70), width=max(1, int(2 * U)))
        if t > 1.4:
            for k in range(2):
                u = ((t * .55 + j * .17 + k * .5) % 1.0)
                px, py = lerp(a[0], b[0], u), lerp(a[1], b[1], u)
                o = orb(int(16 * U), (200, 210, 255), .9)
                _paste_any(L, o, int(px - o.width / 2), int(py - o.height / 2))
    for i, nm in enumerate(left):
        p = eo(prog(t, .3 + i * .1, .8 + i * .1))
        y = cy + (i - 1) * (ch + 26 * U) - ch / 2
        T = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        glass(T, bg, (lx, y, lx + cw, y + ch), int(14 * U))
        tile(T, lx + 14 * U, y + ch / 2 - 18 * U, int(36 * U), ["violet", "slate", "teal"][i], "spark" if i == 0 else ("cmd" if i == 1 else "bolt"))
        text(ImageDraw.Draw(T), (lx + 62 * U, y + ch / 2), nm, font("m", 18 * U), TXT, "lm")
        L.alpha_composite(transform(opacity(T, p), 1, (1 - p) * -40 * U, 0))
    for i, (nm, g) in enumerate(right):
        p = eo(prog(t, .5 + i * .1, 1.0 + i * .1))
        hh = ch * .82
        y = cy + (i - 1.5) * (hh + 20 * U) - hh / 2
        T = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        glass(T, bg, (rx, y, rx + cw, y + hh), int(13 * U))
        glyph(ImageDraw.Draw(T), g, rx + 30 * U, y + hh / 2, 22 * U, SUB)
        text(ImageDraw.Draw(T), (rx + 56 * U, y + hh / 2), nm, font("m", 17 * U), TXT, "lm")
        L.alpha_composite(transform(opacity(T, p), 1, (1 - p) * 40 * U, 0))
    # hub
    hp = back(prog(t, .2, .9), 1.5)
    if hp > 0:
        g_ = orb(int(170 * U), ACC, .5 + .15 * math.sin(t * 3))
        _paste_any(L, g_, int(W / 2 - g_.width / 2), int(cy - g_.height / 2))
        size = hub * clamp(hp, 0, 1.2)
        lg = logo_img(max(2, size))
        L.alpha_composite(lg, (int(W / 2 - size / 2), int(cy - size / 2)))
        d = ImageDraw.Draw(L)
        text(d, (W / 2, cy + hub / 2 + 30 * U), "Mirage bridge", font("s", 17 * U), TXT, "mm", int(255 * clamp(hp)))
        text(d, (W / 2, cy + hub / 2 + 54 * U), "127.0.0.1 only", font("r", 14 * U), FAINT, "mm", int(255 * clamp(hp)))
    # approval dialog
    ap = back(prog(t, 3.0, 3.5), 1.3)
    if ap > 0:
        D = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        dw = int(520 * U); dh = int(220 * U)
        dx = W / 2 - dw / 2; dy = cy - dh / 2
        shadow(D, (dx, dy, dx + dw, dy + dh), int(16 * U), 180, int(40 * U), int(20 * U))
        paste_rr(D, (dx, dy), dw, dh, int(16 * U), ((30, 31, 46), (17, 18, 27)), 252, 90)
        ring(D, (dx, dy), dw, dh, int(16 * U), (255, 255, 255), 55)
        tile(D, dx + 22 * U, dy + 22 * U, int(40 * U), "violet", "spark")
        dd = ImageDraw.Draw(D)
        text(dd, (dx + 76 * U, dy + 32 * U), "An AI agent wants to act", font("s", 19 * U), TXT, "lm")
        text(dd, (dx + 76 * U, dy + 55 * U), "Claude Desktop · paste_text", font("r", 14 * U), SUB, "lm")
        paste_rr(D, (dx + 22 * U, dy + 82 * U), dw - 44 * U, 50 * U, int(10 * U), (255, 255, 255), 18)
        dd = ImageDraw.Draw(D)
        text(dd, (dx + 38 * U, dy + 107 * U), "Paste 212 characters into Outlook", font("r", 16 * U), TXT, "lm")
        bx = dx + dw - 22 * U
        clicked = t > 4.6
        for lab, style in [("Allow once", "acc"), ("Always allow", "ghost"), ("Deny", "ghost")]:
            fw = dd.textlength(lab, font=font("s", 15 * U)) + 34 * U
            bx -= fw
            if style == "acc":
                paste_rr(D, (bx, dy + dh - 62 * U), fw, 40 * U, int(10 * U), "acc", 255)
                if clicked: paste_rr(D, (bx, dy + dh - 62 * U), fw, 40 * U, int(10 * U), (255, 255, 255), int(90 * math.sin(prog(t, 4.6, 5.0) * math.pi)))
            else:
                ring(D, (bx, dy + dh - 62 * U), fw, 40 * U, int(10 * U), (255, 255, 255), 60)
            dd = ImageDraw.Draw(D)
            text(dd, (bx + fw / 2, dy + dh - 42 * U), lab, font("s", 15 * U), (255, 255, 255) if style == "acc" else SUB, "mm")
            bx -= 10 * U
        out = eo(prog(t, 5.0, 5.4))
        D = opacity(D, clamp(ap) * (1 - out))
        L.alpha_composite(transform(D, lerp(.9, 1, clamp(ap)) - .04 * out, cx=W / 2, cy=dy + dh / 2) if ap < 1 or out > 0 else D)
    # check badges after approval
    if t > 5.2:
        d = ImageDraw.Draw(L)
        bp = eo(prog(t, 5.2, 5.7))
        items = ["Local only", "Your token", "You approve risky actions"]
        fb = font("m", 16 * U)
        ws = [d.textlength(s_, font=fb) + 56 * U for s_ in items]
        x = W / 2 - (sum(ws) + 16 * U * 2) / 2
        y = cy + (150 if wide else 300) * U + (H * .0)
        y = min(H - 90 * U, cy + hub / 2 + 96 * U)
        for s_, w_ in zip(items, ws):
            paste_rr(L, (x, y), w_, 40 * U, int(20 * U), (255, 255, 255), int(20 * bp))
            ring(L, (x, y), w_, 40 * U, int(20 * U), GRN, int(120 * bp))
            d = ImageDraw.Draw(L)
            glyph(d, "check", x + 22 * U, y + 20 * U, 14 * U, GRN, int(255 * bp))
            text(d, (x + 38 * U, y + 20 * U), s_, fb, TXT, "lm", int(255 * bp))
            x += w_ + 16 * U
    return L

# ---------- touchpad-first pieces ----------
def cursor(layer, x, y, press=0.0, a=255):
    s = 2.3 * U * (1 - .12 * press)
    pts = [(0, 0), (0, 17), (4.2, 13.1), (7.2, 20), (9.8, 18.9), (6.9, 12.2), (12.3, 12.2)]
    P = [(x + px * s, y + py * s) for px, py in pts]
    sh = Image.new("RGBA", layer.size, (0, 0, 0, 0))
    ImageDraw.Draw(sh).polygon([(px + 3 * U, py + 5 * U) for px, py in P], fill=(0, 0, 0, int(a * .45)))
    layer.alpha_composite(sh.filter(ImageFilter.GaussianBlur(5 * U)))
    d = ImageDraw.Draw(layer)
    d.polygon(P, fill=(255, 255, 255, a), outline=(10, 10, 16, a))
    d.line(P + [P[0]], fill=(10, 10, 16, a), width=max(1, int(1.6 * U)), joint="curve")

def ripple(layer, x, y, p, color=ACC, size=60):
    if p <= 0 or p >= 1: return
    r = (12 + size * eo(p)) * U
    m = int(r + 8 * U)
    R_ = Image.new("RGBA", (2 * m, 2 * m), (0, 0, 0, 0))
    d = ImageDraw.Draw(R_)
    r2 = r * .55
    d.ellipse((m - r2, m - r2, m + r2, m + r2), fill=color + (int(70 * (1 - p)),))
    d.ellipse((m - r, m - r, m + r, m + r), outline=color + (int(220 * (1 - p)),), width=max(2, int(3 * U)))
    _paste_any(layer, R_, int(x - m), int(y - m))

def path(t, pts):
    """pts: [(time, x, y)] keyframes, eased between them."""
    if t <= pts[0][0]: return pts[0][1], pts[0][2]
    for (t0, x0, y0), (t1, x1, y1) in zip(pts, pts[1:]):
        if t <= t1:
            k = eio(prog(t, t0, t1)); return lerp(x0, x1, k), lerp(y0, y1, k)
    return pts[-1][1], pts[-1][2]

def corner_glow(layer, p, corner="tl"):
    if p <= 0: return
    rad = int(320 * U)
    o = orb(rad, ACC, .75 * p)
    x = -rad if corner[1] == "l" else W - rad
    y = -rad if corner[0] == "t" else H - rad
    _paste_any(layer, o, x, y)
    o2 = orb(int(150 * U), VIO, .8 * p)
    _paste_any(layer, o2, int(x + rad - 150 * U), int(y + rad - 150 * U))

def inset_box():
    if W > H:
        w, h = int(240 * U), int(160 * U)
        return int(W - w - 34 * U), int(H - h - 74 * U), w, h
    w, h = int(210 * U), int(136 * U)
    return int(W / 2 - w / 2), int(H - h - 64 * U), w, h

def touchpad(layer, bg, fingers, label="", a=1.0, box=None):
    """fingers: list of (fx, fy, strength) in 0..1 pad coordinates."""
    if a <= 0: return
    x, y, w, h = box or inset_box()
    T = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    shadow(T, (x, y, x + w, y + h), int(20 * U), 120, int(30 * U), int(14 * U))
    glass(T, bg, (x, y, x + w, y + h), int(20 * U), tint=(22, 24, 38), strength=.7)
    ring(T, (x + 6 * U, y + 6 * U), w - 12 * U, h - 12 * U, int(15 * U), (255, 255, 255), 22)
    for fx, fy, st in fingers:
        if st <= 0: continue
        cx_, cy_ = x + 14 * U + fx * (w - 28 * U), y + 14 * U + fy * (h - 28 * U)
        g_ = orb(int(34 * U), (170, 180, 255), .75 * st)
        _paste_any(T, g_, int(cx_ - g_.width / 2), int(cy_ - g_.height / 2))
        r = 11 * U
        ImageDraw.Draw(T).ellipse((cx_ - r, cy_ - r, cx_ + r, cy_ + r), fill=(255, 255, 255, int(235 * st)))
    if label:
        d = ImageDraw.Draw(T)
        f = font("s", 15 * U); lw = d.textlength(label, font=f) + 30 * U
        paste_rr(T, (x + w / 2 - lw / 2, y + h + 14 * U), lw, 32 * U, int(16 * U), (255, 255, 255), 24)
        ring(T, (x + w / 2 - lw / 2, y + h + 14 * U), lw, 32 * U, int(16 * U), ACC, 110)
        d = ImageDraw.Draw(T)
        text(d, (x + w / 2, y + h + 30 * U), label, f, (205, 210, 255), "mm")
    layer.alpha_composite(opacity(T, a) if a < 1 else T)

def swipe_fingers(p, n=2, dx=-1, dy=0):
    """n fingers moving across the pad as p goes 0..1 (strength fades in and out)."""
    if p <= 0 or p >= 1: return []
    st = math.sin(p * math.pi) ** .6
    k = eio(p)
    cx_ = .5 - dx * .28 + dx * .56 * k; cy_ = .5 - dy * .3 + dy * .6 * k
    sp = .17
    offs = [(-sp / 2, 0), (sp / 2, 0)] if n == 2 else [(-sp, .04), (0, -.04), (sp, .04)] if n == 3 else [(-1.5 * sp, .06), (-.5 * sp, -.03), (.5 * sp, -.03), (1.5 * sp, .06)]
    if dy: offs = [(ox, oy) for ox, oy in offs]
    return [(cx_ + ox, cy_ + oy * 1.6, st) for ox, oy in offs]

def tap_fingers(p, n=3):
    if p <= 0 or p >= 1: return []
    st = math.sin(p * math.pi)
    sp = .2
    offs = [(-sp, .06), (0, -.05), (sp, .06)] if n == 3 else [(-1.5 * sp, .08), (-.5 * sp, -.04), (.5 * sp, -.04), (1.5 * sp, .08)]
    return [(.5 + ox, .5 + oy * 1.4, st) for ox, oy in offs]

SECTIONS = ["Apps", "Workspaces", "Clipboard", "Notes", "AI"]
SEC_TILES = {
    "Apps": [("VS Code", "blue", "braces", "Pinned"), ("Terminal", "slate", "cmd", "Pinned"), ("Spotify", "green", "music", ""), ("Notion", "slate", "note", ""),
             ("Figma", "rose", "grid", ""), ("Switch window", "violet", "window", ""), ("Find a file", "teal", "file", ""), ("Calculator", "amber", "calc", ""),
             ("Clipboard", "violet", "clip", ""), ("Ask AI", "violet", "spark", "AI")],
    "Workspaces": [("Focus", "teal", "stack", "4 apps"), ("Design review", "rose", "stack", "3 apps"), ("Morning", "amber", "stack", "5 apps"),
                   ("Writing", "violet", "stack", "2 apps"), ("Streaming", "green", "stack", "3 apps"), ("Save open windows", "slate", "stack", "New")],
    "Clipboard": [("Meeting moved to 3:30", "violet", "clip", "2 min"), ("npm run build", "slate", "cmd", "8 min"), ("#7C8CFF", "blue", "clip", "Color"),
                  ("github.com/…/pulls", "blue", "globe", "Link"), ("Thanks, sending it now!", "violet", "clip", "1 hr"), ("Invoice 2041.pdf", "teal", "file", "File"),
                  ("Address for Saturday", "violet", "clip", "Yesterday")],
    "Notes": [("New note", "rose", "note", "Drop here"), ("Launch checklist", "amber", "note", ""), ("Ideas", "amber", "note", ""), ("Groceries", "amber", "note", "")],
    "AI": [("ChatGPT", "green", "spark", "Default"), ("Claude", "amber", "spark", ""), ("Gemini", "blue", "spark", ""), ("Copilot", "teal", "spark", ""),
           ("Perplexity", "slate", "spark", ""), ("Agent bridge", "violet", "shield", "Off")],
}

def home_geom(cx, top, width):
    s = U; head = 80 * s; pillh = 56 * s; pad = 18 * s; gap = 12 * s; foot = 44 * s; cols = 5
    tw = (width - 2 * pad - (cols - 1) * gap) / cols; th = tw * (.66 if W > H else .72)
    gy = top + head + pillh + pad * .5
    hgt = head + pillh + pad * .5 + 2 * th + gap + pad + foot
    x0 = cx - width / 2
    rects = [(x0 + pad + (i % cols) * (tw + gap), gy + (i // cols) * (th + gap), tw, th) for i in range(10)]
    return dict(x0=x0, y0=top, w=width, h=hgt, head=head, pillh=pillh, foot=foot, tw=tw, th=th, rects=rects, gy=gy, gh=2 * th + gap, pad=pad)

def tile_card(layer, rect, item, hover=0.0, a=1.0, dx=0.0, pressed=0.0):
    x, y, w, h = rect
    sc = 1 + .06 * hover - .04 * pressed
    cxm, cym = x + w / 2 + dx, y + h / 2 - 4 * U * hover
    w2, h2 = w * sc, h * sc
    x, y = cxm - w2 / 2, cym - h2 / 2
    nm, tint, g, badge = item
    T = Image.new("RGBA", (int(w2 + 60 * U), int(h2 + 60 * U)), (0, 0, 0, 0))
    o = 30 * U
    if hover > 0:
        shadow(T, (o, o + 6 * U, o + w2, o + h2 + 6 * U), int(16 * U), int(120 * hover), int(18 * U), int(8 * U))
    paste_rr(T, (o, o), w2, h2, int(16 * U), (255, 255, 255), int(14 + 16 * hover))
    if hover > 0:
        paste_rr(T, (o, o), w2, h2, int(16 * U), ((124, 140, 255), (192, 132, 252)), int(60 * hover), 135)
    ring(T, (o, o), w2, h2, int(16 * U), (200, 195, 255) if hover > .3 else (255, 255, 255), int(34 + 110 * hover))
    isz = int(min(46 * U, h2 * .38) * sc)
    tile(T, o + w2 / 2 - isz / 2, o + h2 * .16, isz, tint, g)
    d = ImageDraw.Draw(T)
    f = font("m", (16.5 if W > H else 15) * U)
    nm_ = nm
    while d.textlength(nm_, font=f) > w2 - 20 * U and len(nm_) > 4: nm_ = nm_[:-2] + "…" if not nm_.endswith("…") else nm_[:-2] + "…"
    text(d, (o + w2 / 2, o + h2 * .16 + isz + 20 * U), nm_, f, TXT, "mm")
    if badge:
        text(d, (o + w2 / 2, o + h2 * .16 + isz + 42 * U), badge, font("r", 13 * U), FAINT, "mm")
    if g_pinned(badge):
        glyph(d, "pin", o + w2 - 18 * U, o + 18 * U, 13 * U, SUB)
    layer.alpha_composite(opacity(T, a) if a < 1 else T, (int(x - o), int(y - o)))

def g_pinned(b): return b == "Pinned"

def tile_home(layer, bg, cx, top, width, t, sec=0.0, items=None, next_items=None, slide=0.0, slide_dir=1, hover=None, appear=None,
              drop=0.0, pressed=None, search_hint="Search, or swipe for sections"):
    """sec: section index (fractional while the pill moves). hover: (index, amount). appear: stagger time (t - start)."""
    G = home_geom(cx, top, width); s = U
    x0, y0, w, hgt = G["x0"], G["y0"], G["w"], G["h"]
    shadow(layer, (x0, y0, x0 + w, y0 + hgt), int(16 * s), 150, int(50 * s), int(26 * s))
    glass(layer, bg, (x0, y0, x0 + w, y0 + hgt), int(16 * s))
    d = ImageDraw.Draw(layer)
    head = G["head"]; ts = int(38 * s)
    paste_rr(layer, (x0 + 22 * s, y0 + (head - ts) / 2), ts, ts, int(11 * s), "acc")
    d = ImageDraw.Draw(layer)
    glyph(d, "search", x0 + 22 * s + ts / 2, y0 + head / 2, ts * .46)
    text(d, (x0 + 22 * s + ts + 18 * s, y0 + head / 2), search_hint, font("r", 25 * s), FAINT, "lm")
    # gear
    glyph(d, "gear", x0 + w - 40 * s, y0 + head / 2, 20 * s, SUB)
    d.line((x0 + 1, y0 + head, x0 + w - 2, y0 + head), fill=(255, 255, 255, 26), width=1)
    # section pills
    fp = font("m", (15.5 if W > H else 14) * s)
    py = y0 + head + 12 * s; ph = 34 * s
    pws = [d.textlength(n, font=fp) + 32 * s for n in SECTIONS]
    px = [x0 + 22 * s]
    for pw in pws[:-1]: px.append(px[-1] + pw + 8 * s)
    i0 = int(math.floor(sec)); fr = sec - i0; i1 = min(i0 + 1, len(SECTIONS) - 1)
    hx = lerp(px[i0], px[i1], fr); hw = lerp(pws[i0], pws[i1], fr)
    for i, n in enumerate(SECTIONS):
        ring(layer, (px[i], py), pws[i], ph, int(17 * s), (255, 255, 255), 30)
    paste_rr(layer, (hx, py), hw, ph, int(17 * s), ((124, 140, 255), (192, 132, 252)), 235, 0)
    d = ImageDraw.Draw(layer)
    for i, n in enumerate(SECTIONS):
        act = 1 - min(1, abs(sec - i))
        text(d, (px[i] + pws[i] / 2, py + ph / 2), n, fp, mix(SUB, (255, 255, 255), act), "mm")
    # swipe hint dots on the right
    text(d, (x0 + w - 22 * s, py + ph / 2), "‹  swipe  ›", font("r", 13.5 * s), FAINT, "rm")
    # tiles
    items = items if items is not None else SEC_TILES["Apps"]
    sw = w * .35
    def draw_set(its, off, fade, stagger_base):
        for i, it in enumerate(its[:10]):
            r = G["rects"][i]
            a_ = fade
            dy_ = 0
            if appear is not None:
                p = back(prog(appear, stagger_base + .045 * i, stagger_base + .045 * i + .45), 1.4)
                a_ *= clamp(p)
                if p <= 0: continue
                r = (r[0], r[1] + (1 - clamp(p)) * 26 * s, r[2], r[3])
            hv = hover[1] if hover and hover[0] == i and off == 0 else 0.0
            pr = pressed[1] if pressed and pressed[0] == i and off == 0 else 0.0
            tile_card(layer, r, it, hv, a_, off + (i % 5) * off * .08, pr)
    if next_items is not None and slide > 0:
        k = eio(slide)
        draw_set(items, -slide_dir * sw * k, 1 - k, 0)
        draw_set(next_items, slide_dir * sw * (1 - k), k, 0)
    else:
        draw_set(items, 0, 1.0, 0)
    # drop overlay
    if drop > 0:
        gx0, gy0 = x0 + G["pad"], G["gy"] - 6 * s; gw, gh = w - 2 * G["pad"], G["gh"] + 12 * s
        paste_rr(layer, (gx0, gy0), gw, gh, int(18 * s), (14, 15, 26), int(242 * drop))
        paste_rr(layer, (gx0, gy0), gw, gh, int(18 * s), ((124, 140, 255), (192, 132, 252)), int(40 * drop), 45)
        d = ImageDraw.Draw(layer)
        dash = 14 * s; per = gw * 2 + gh * 2
        for k in range(int(per / (dash * 2))):
            u = k * dash * 2 + (t * 60 * s) % (dash * 2)
            seg = []
            for uu in (u, u + dash):
                uu %= per
                if uu < gw: seg.append((gx0 + uu, gy0))
                elif uu < gw + gh: seg.append((gx0 + gw, gy0 + uu - gw))
                elif uu < 2 * gw + gh: seg.append((gx0 + gw - (uu - gw - gh), gy0 + gh))
                else: seg.append((gx0, gy0 + gh - (uu - 2 * gw - gh)))
            if abs(seg[0][0] - seg[1][0]) + abs(seg[0][1] - seg[1][1]) < dash * 1.5:
                d.line(seg, fill=(170, 165, 255, int(230 * drop)), width=max(2, int(2.5 * s)))
        bob = math.sin(t * 5) * 5 * s
        tile(layer, gx0 + gw / 2 - 34 * s, gy0 + gh / 2 - 70 * s + bob, int(68 * s), "violet", "file")
        d = ImageDraw.Draw(layer)
        text(d, (gx0 + gw / 2, gy0 + gh / 2 + 32 * s), "Drop it here", font("dB", 30 * s), TXT, "mm", int(255 * drop))
        text(d, (gx0 + gw / 2, gy0 + gh / 2 + 66 * s), "Summarize it, send it to AI, or add it to a workspace", font("r", 16 * s), SUB, "mm", int(255 * drop))
    # footer
    fy = y0 + hgt - G["foot"]
    d.line((x0 + 1, fy, x0 + w - 2, fy), fill=(255, 255, 255, 24), width=1)
    text(d, (x0 + 22 * s, fy + G["foot"] / 2), "Mirage", font("s", 14 * s), ACC, "lm")
    text(d, (x0 + w - 22 * s, fy + G["foot"] / 2), "Click to open    Right-click for more    Swipe for sections", font("r", 14 * s), FAINT, "rm")
    return G

def tile_center(G, i):
    x, y, w, h = G["rects"][i]
    return x + w * .58, y + h * .55

def home_top(): return ltop() - (10 if W > H else 0) * U

# --- scene: hot corner + tiles ---
def scene_corner(bg, t):
    L = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    caption(L, t, "TOUCHPAD FIRST", "No keyboard. Just flick to the corner.", "Rest the pointer top left, or tap three fingers. Alt + Space works too.", capy(), .15, leave=None)
    pw = panel_w(); top = home_top()
    G = home_geom(W / 2, top, pw)
    # cursor: glide to corner, then down to the tiles
    c1 = tile_center(G, 0); c2 = tile_center(G, 2); c3 = tile_center(G, 5)
    cx_, cy_ = path(t, [(0, W * .62, H * .64), (.5, W * .62, H * .64), (1.7, 8 * U, 8 * U), (2.6, 8 * U, 8 * U),
                        (3.5, c1[0], c1[1]), (4.3, c1[0], c1[1]), (4.9, c2[0], c2[1]), (5.4, c2[0], c2[1])])
    cg = eo(prog(t, 1.5, 1.9)) * (1 - eo(prog(t, 2.6, 3.4)))
    corner_glow(L, cg)
    ripple(L, 8 * U, 8 * U, prog(t, 1.85, 2.6), ACC, 120)
    # pad: one finger slides up-left, then a three-finger tap as the alternative
    fingers = []
    p1 = prog(t, .45, 1.8)
    if 0 < p1 < 1:
        k = eio(p1); fingers = [(lerp(.62, .12, k), lerp(.66, .14, k), math.sin(p1 * math.pi) ** .5)]
    tp = touchpad_alpha = eo(prog(t, .2, .6)) * (1 - eo(prog(t, 2.3, 2.7)))
    touchpad(L, bg, fingers, "Pointer to the corner", tp)
    lp = back(prog(t, 2.0, 2.55), 1.25)
    if lp > 0:
        P = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        hv = None
        if 3.4 < t < 4.5: hv = (0, eo(prog(t, 3.35, 3.6)) * (1 - eo(prog(t, 4.3, 4.55))))
        elif t >= 4.7: hv = (2, eo(prog(t, 4.75, 5.0)))
        pr = (2, math.sin(prog(t, 5.55, 5.85) * math.pi)) if t > 5.55 else None
        tile_home(P, bg, W / 2, top, pw, t, 0, SEC_TILES["Apps"], hover=hv, appear=t - 2.15, pressed=pr)
        if lp < 1:
            P = transform(opacity(P, clamp(lp)), lerp(.55, 1, clamp(lp)) if lp < 1 else 1, lerp(-W * .3, 0, clamp(lp)), lerp(-H * .25, 0, clamp(lp)), cx=W / 2, cy=top)
        L.alpha_composite(P)
    ripple(L, c2[0], c2[1], prog(t, 5.55, 6.2), ACC, 70)
    if t > 5.9:
        tp2 = eo(prog(t, 5.9, 6.2))
        d = ImageDraw.Draw(L)
        msg = "Spotify opened"
        f = font("m", 17 * U); mw = d.textlength(msg, font=f) + 70 * U
        mx, my = W / 2 - mw / 2, G["y0"] + G["h"] + 26 * U
        paste_rr(L, (mx, my), mw, 44 * U, int(22 * U), (24, 26, 40), int(235 * tp2))
        ring(L, (mx, my), mw, 44 * U, int(22 * U), GRN, int(130 * tp2))
        d = ImageDraw.Draw(L)
        glyph(d, "check", mx + 26 * U, my + 22 * U, 14 * U, GRN, int(255 * tp2))
        text(d, (mx + 44 * U, my + 22 * U), msg, f, TXT, "lm", int(255 * tp2))
    cursor(L, cx_, cy_, math.sin(prog(t, 5.55, 5.85) * math.pi))
    return L

# --- scene: two-finger swipe between sections ---
def scene_swipe(bg, t):
    L = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    caption(L, t, "SECTIONS", "Swipe through your day.", "Two fingers sideways: apps, workspaces, clipboard, notes, AI.", capy(), .15)
    pw = panel_w(); top = home_top()
    s1 = prog(t, 1.0, 1.55); s2 = prog(t, 2.6, 3.15); s3 = prog(t, 4.0, 4.55)
    if t < 2.6: sec = eio(s1); cur, nxt, sl = "Apps", "Workspaces", s1
    elif t < 4.0: sec = 1 + eio(s2); cur, nxt, sl = "Workspaces", "Clipboard", s2
    else: sec = 2 + 2 * eio(s3); cur, nxt, sl = "Clipboard", "AI", s3
    if sl >= 1: cur, nxt, sl = nxt, None, 0
    G = home_geom(W / 2, top, pw)
    hv = None
    if 3.3 < t < 3.95: hv = (0, eo(prog(t, 3.3, 3.5)) * (1 - eo(prog(t, 3.75, 3.95))))
    if t > 4.7: hv = (1, eo(prog(t, 4.75, 4.95)))
    tile_home(L, bg, W / 2, top, pw, t, sec, SEC_TILES[cur], SEC_TILES[nxt] if nxt else None, sl, 1, hover=hv)
    fing = swipe_fingers(prog(t, .85, 1.6), 2, -1) or swipe_fingers(prog(t, 2.45, 3.2), 2, -1) or swipe_fingers(prog(t, 3.85, 4.6), 2, -1)
    touchpad(L, bg, fing, "Two-finger swipe", eo(prog(t, .2, .6)))
    cx_, cy_ = path(t, [(0, W * (.56 if W > H else .86), H * .93), (3.2, W * (.56 if W > H else .86), H * .93), (3.4, *tile_center(G, 0)), (4.6, *tile_center(G, 0)), (4.85, *tile_center(G, 1))])
    cursor(L, cx_, cy_)
    return L

# --- scene: radial quick menu ---
RADIAL = [("Search", "search", "blue"), ("Selected text", "snip", "violet"), ("Ask AI", "spark", "teal"), ("Clipboard", "clip", "amber"),
          ("Quick note", "note", "rose"), ("Workspaces", "stack", "green"), ("Windows", "window", "slate")]

def radial(layer, bg, cx, cy, t0, t, hot=-1, hot_p=0.0, close=0.0):
    ring_r = 150 * U; bub = 76 * U; hub = 64 * U
    hp = back(prog(t, t0, t0 + .35), 1.6) * (1 - eo(close))
    if hp <= 0: return
    halo = orb(int(250 * U), (120, 130, 255), .28 * clamp(hp))
    _paste_any(layer, halo, int(cx - halo.width / 2), int(cy - halo.height / 2))
    rr = (ring_r + bub * .8) * clamp(hp)
    if rr > 4:
        m = int(rr + 4)
        O = Image.new("RGBA", (2 * m, 2 * m), (0, 0, 0, 0))
        ImageDraw.Draw(O).ellipse((m - rr, m - rr, m + rr, m + rr), fill=(255, 255, 255, int(10 * clamp(hp))), outline=(255, 255, 255, int(40 * clamp(hp))), width=max(1, int(1.5 * U)))
        _paste_any(layer, O, int(cx - m), int(cy - m))
    n = len(RADIAL)
    for i, (nm, g, tint) in enumerate(RADIAL):
        p = back(prog(t, t0 + .04 * i, t0 + .04 * i + .42), 1.7) * (1 - eo(close))
        if p <= 0: continue
        ang = math.radians(-90 + 360 / n * i)
        bx = cx + math.cos(ang) * ring_r * p; by = cy + math.sin(ang) * ring_r * p
        h_ = hot_p if i == hot else 0.0
        sz = bub * (1 + .16 * h_) * clamp(p, 0, 1.2)
        if sz < 8: continue
        B = Image.new("RGBA", (int(sz + 80 * U), int(sz + 80 * U)), (0, 0, 0, 0))
        o = 40 * U
        shadow(B, (o, o + 6 * U, o + sz, o + sz + 6 * U), int(sz / 2), 120, int(16 * U), int(8 * U))
        paste_rr(B, (o, o), sz, sz, int(sz / 2), (26, 28, 44), 235)
        if h_ > 0: paste_rr(B, (o, o), sz, sz, int(sz / 2), ((124, 140, 255), (192, 132, 252)), int(110 * h_), 135)
        ring(B, (o, o), sz, sz, int(sz / 2), (200, 195, 255) if h_ > .3 else (255, 255, 255), int(50 + 150 * h_), max(1, int((1 + 1.5 * h_) * U)))
        isz = int(sz * .52)
        tile(B, o + sz / 2 - isz / 2, o + sz / 2 - isz / 2, isz, tint, g)
        layer.alpha_composite(opacity(B, clamp(p)), (int(bx - sz / 2 - o), int(by - sz / 2 - o)))
        # label pill
        d = ImageDraw.Draw(layer)
        f = font("m", 14.5 * U); lw = d.textlength(nm, font=f) + 22 * U
        ly = by + sz / 2 + 8 * U if math.sin(ang) > -.3 else by - sz / 2 - 36 * U
        paste_rr(layer, (bx - lw / 2, ly), lw, 28 * U, int(14 * U), ((124, 140, 255), (192, 132, 252)) if h_ > .5 else (20, 21, 32), int(230 * clamp(p)), 0)
        d = ImageDraw.Draw(layer)
        text(d, (bx, ly + 14 * U), nm, f, TXT, "mm", int(255 * clamp(p)))
    # hub
    hs = hub * clamp(hp, 0, 1.2)
    paste_rr(layer, (cx - hs / 2, cy - hs / 2), hs, hs, int(hs / 2), (30, 32, 50), int(240 * clamp(hp)))
    ring(layer, (cx - hs / 2, cy - hs / 2), hs, hs, int(hs / 2), ACC, int(140 * clamp(hp)))
    if hs > 8:
        lg = logo_img(hs * .62)
        layer.alpha_composite(opacity(lg, clamp(hp)), (int(cx - hs * .31), int(cy - hs * .31)))

def mock_app(L, t, dim=0.0, title="Message draft", lines=None, hl=None):
    ew = panel_w(); ex = W / 2 - ew / 2; ey = ltop(); eh = int(340 * U)
    E = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    shadow(E, (ex, ey, ex + ew, ey + eh), int(14 * U), 120, int(40 * U), int(20 * U))
    paste_rr(E, (ex, ey), ew, eh, int(14 * U), (24, 25, 36), 250)
    ring(E, (ex, ey), ew, eh, int(14 * U), (255, 255, 255), 40)
    d = ImageDraw.Draw(E)
    for i, c in enumerate([(255, 95, 87), (254, 188, 46), (40, 200, 64)]):
        d.ellipse((ex + 22 * U + i * 22 * U, ey + 20 * U, ex + 34 * U + i * 22 * U, ey + 32 * U), fill=c + (200,))
    text(d, (ex + ew / 2, ey + 26 * U), title, font("m", 15 * U), FAINT, "mm")
    lines = lines or ["Hi team, quick update on the launch.", "The beta is wrapped and feedback has been great.", "Public release is planned for next week.", "Thanks!"]
    for i, ln in enumerate(lines):
        text(d, (ex + 40 * U, ey + 74 * U + i * 46 * U), ln, font("r", 23 * U), (220, 222, 232), "la")
    L.alpha_composite(opacity(E, 1 - dim))
    return ex, ey, ew, eh

def scene_radial(bg, t):
    L = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    caption(L, t, "QUICK MENU", "Everything, right under your finger.", "Tap four fingers or middle click. The menu opens where you are.", capy(), .15)
    ex, ey, ew, eh = mock_app(L, t, .35 + .25 * eo(prog(t, 1.0, 1.4)) - .6 * eo(prog(t, 3.9, 4.3)) * 0)
    mx, my = W / 2 + ew * .08, ey + eh * .62
    ai_i = 2
    ang = math.radians(-90 + 360 / len(RADIAL) * ai_i)
    tx_, ty_ = mx + math.cos(ang) * 150 * U, my + math.sin(ang) * 150 * U
    cx_, cy_ = path(t, [(0, W * .7, H * .8), (.5, W * .7, H * .8), (1.1, mx, my), (2.3, mx, my), (2.9, tx_, ty_)])
    hot_p = eo(prog(t, 2.35, 2.85))
    close = prog(t, 3.55, 3.85)
    radial(L, bg, mx, my, 1.4, t, ai_i, hot_p, close)
    ripple(L, tx_, ty_, prog(t, 3.3, 3.9), TEAL, 70)
    touchpad(L, bg, tap_fingers(prog(t, .95, 1.45), 4), "Four-finger tap", eo(prog(t, .2, .6)) * (1 - eo(prog(t, 3.4, 3.8))))
    # AI section appears
    ap = back(prog(t, 3.75, 4.25), 1.3)
    if ap > 0:
        P = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        top = home_top()
        G = tile_home(P, bg, W / 2, top, panel_w(), t, 4, SEC_TILES["AI"], hover=(1, eo(prog(t, 4.6, 4.9))) if t > 4.6 else None, appear=t - 3.8,
                      search_hint="Ask anything, or pick an AI")
        P = transform(opacity(P, clamp(ap)), lerp(.9, 1, clamp(ap)), cx=tx_, cy=ty_) if ap < 1 else P
        L.alpha_composite(P)
        c = tile_center(G, 1)
        cx_, cy_ = path(t, [(4.0, tx_, ty_), (4.7, c[0], c[1])])
    cursor(L, cx_, cy_, math.sin(prog(t, 3.3, 3.6) * math.pi))
    return L

# --- scene: drag a file to the corner and drop it ---
DROP_ROWS = [Row("Summarize with ChatGPT", "Reads the file and asks for the key points", "AI", "violet", "spark"),
             Row("Send to AI", "Opens your AI with the file ready to paste", "AI", "violet", "spark"),
             Row("Add to workspace \"Focus\"", "Opens with the rest of the workspace", "Workspace", "teal", "stack"),
             Row("Pin to home", "A tile on the Apps section", "Pin", "rose", "pin"),
             Row("Show in folder", "Downloads", "File", "slate", "file")]

def file_card(layer, bg, x, y, sc=1.0, a=1.0, tilt=0.0):
    w, h = 330 * U * sc, 92 * U * sc
    T = Image.new("RGBA", (int(w + 80 * U), int(h + 80 * U)), (0, 0, 0, 0))
    o = 40 * U
    shadow(T, (o, o + 8 * U, o + w, o + h + 8 * U), int(16 * U), 140, int(22 * U), int(10 * U))
    paste_rr(T, (o, o), w, h, int(16 * U * sc), ((40, 42, 62), (24, 25, 38)), 245, 90)
    ring(T, (o, o), w, h, int(16 * U * sc), (255, 255, 255), 60)
    tile(T, o + 16 * U * sc, o + h / 2 - 28 * U * sc, int(56 * U * sc), "rose", "file")
    d = ImageDraw.Draw(T)
    text(d, (o + 88 * U * sc, o + h / 2 - 12 * U * sc), "Q3 report.pdf", font("s", 20 * U * sc), TXT, "lm")
    text(d, (o + 88 * U * sc, o + h / 2 + 14 * U * sc), "PDF · 2.4 MB", font("r", 14.5 * U * sc), FAINT, "lm")
    if tilt: T = T.rotate(tilt, resample=Image.BICUBIC, expand=False)
    layer.alpha_composite(opacity(T, a) if a < 1 else T, (int(x - o), int(y - o)))
    return w, h

def scene_drop(bg, t):
    L = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    caption(L, t, "DRAG AND DROP", "Drop a file. Pick what happens.", "Drag it to the corner to open Mirage, then let go on any tile.", capy(), .15)
    top = home_top(); pw = panel_w()
    G = home_geom(W / 2, top, pw)
    fx0, fy0 = (W * .56, H * .58) if W > H else (W * .34, H * .62)
    grab = (fx0 + 40 * U, fy0 + 46 * U)
    mid = (W / 2, G["gy"] + G["gh"] / 2)
    cx_, cy_ = path(t, [(0, W * .82, H * .86), (.55, *grab), (.8, *grab), (2.0, 10 * U, 10 * U), (2.6, 10 * U, 10 * U), (3.4, *mid), (3.6, *mid)])
    dragging = .8 < t < 3.65
    corner_glow(L, eo(prog(t, 1.8, 2.15)) * (1 - eo(prog(t, 2.7, 3.3))))
    ripple(L, 10 * U, 10 * U, prog(t, 2.0, 2.7), ACC, 110)
    hp = back(prog(t, 2.25, 2.75), 1.25)
    lo = eo(prog(t, 3.9, 4.25))
    if hp > 0 and lo < 1:
        P = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        dropv = eo(prog(t, 2.4, 2.8)) * (1 - eo(prog(t, 3.65, 3.95)))
        tile_home(P, bg, W / 2, top, pw, t, 0, SEC_TILES["Apps"], drop=dropv)
        P = transform(opacity(P, clamp(hp) * (1 - lo)), lerp(.55, 1, clamp(hp)) if hp < 1 else 1, lerp(-W * .3, 0, clamp(hp)) if hp < 1 else 0,
                      lerp(-H * .25, 0, clamp(hp)) if hp < 1 else 0, cx=W / 2, cy=top)
        L.alpha_composite(P)
    if lo > 0:
        P = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        sel_glow = math.sin(prog(t, 5.3, 5.8) * math.pi)
        launcher(P, bg, W / 2, top, pw, "", DROP_ROWS, 0 if t > 4.6 else -1, t, chip=("Dropped file", "file"), caret=False,
                 placeholder="What should I do with Q3 report.pdf?", selection_text="Q3 report.pdf  ·  Downloads", sel_glow=sel_glow)
        L.alpha_composite(transform(opacity(P, lo), lerp(.97, 1, lo), cy=top) if lo < 1 else P)
        rowc = (W / 2 + pw * .1, top + 80 * U + 58 * U + 10 * U + 28 * U)
        cx_, cy_ = path(t, [(4.1, *mid), (4.6, *rowc)])
        ripple(L, rowc[0], rowc[1], prog(t, 5.3, 5.9), ACC, 70)
    # the file card
    if t < 3.9:
        if t < .8: fx, fy, sc, tilt, a = fx0, fy0, 1.0, 0, 1.0
        else:
            k = eo(prog(t, .8, 1.1))
            fx, fy = cx_ - 40 * U * lerp(1, .8, k), cy_ - 46 * U * lerp(1, .8, k)
            sc = lerp(1, .8, k) * (1 - .9 * eo(prog(t, 3.6, 3.85))); tilt = -4 * k; a = lerp(1, .9, k) * (1 - eo(prog(t, 3.6, 3.85)))
        if sc > .05: file_card(L, bg, fx, fy, sc, a, tilt)
    ripple(L, mid[0], mid[1], prog(t, 3.6, 4.2), VIO, 90)
    cursor(L, cx_, cy_, .6 if dragging else math.sin(prog(t, 5.3, 5.6) * math.pi))
    return L

FEATURES = [("Hot corner & gestures", "touch", "blue"), ("Radial quick menu", "radial", "violet"), ("Swipe between sections", "swipe", "teal"), ("Drag and drop", "file", "amber"),
            ("Clipboard history", "clip", "violet"), ("Window switcher", "window", "violet"), ("Workspaces", "stack", "teal"), ("Snippets", "snip", "amber"),
            ("Ask any AI", "spark", "violet"), ("File search", "search", "teal"), ("Keyboard too", "key", "slate"), ("No admin needed", "shield", "green")]

def scene_grid(bg, t):
    L = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    caption(L, t, "ALL IN ONE", "Everything, one gesture away.", "", capy(), .15)
    wide = W > H
    cols = 4 if wide else 3
    tw = int((300 if wide else 290) * U); th = int((112 if wide else 130) * U); gap = int(22 * U)
    rows_ = math.ceil(len(FEATURES) / cols)
    gx = W / 2 - (cols * tw + (cols - 1) * gap) / 2; gy = ltop() - (20 if wide else 0) * U
    for i, (nm, g, tint) in enumerate(FEATURES):
        p = back(prog(t, .35 + i * .07, .85 + i * .07), 1.5)
        if p <= 0: continue
        x = gx + (i % cols) * (tw + gap); y = gy + (i // cols) * (th + gap)
        T = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        glass(T, bg, (x, y, x + tw, y + th), int(16 * U))
        if wide:
            tile(T, x + 20 * U, y + th / 2 - 24 * U, int(48 * U), tint, g)
            text(ImageDraw.Draw(T), (x + 84 * U, y + th / 2), nm, font("s", 19 * U), TXT, "lm")
        else:
            tile(T, x + tw / 2 - 24 * U, y + 18 * U, int(48 * U), tint, g)
            text(ImageDraw.Draw(T), (x + tw / 2, y + th - 30 * U), nm, font("s", 18 * U), TXT, "mm")
        pc = clamp(p)
        L.alpha_composite(transform(opacity(T, pc), lerp(.85, 1, min(p, 1.05)), 0, (1 - pc) * 30 * U, cx=x + tw / 2, cy=y + th / 2) if p != 1 else T)
    return L

def scene_outro(bg, t):
    L = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    cy = H * (.33 if W > H else .32)
    s = back(prog(t, .1, .9), 1.3)
    size = 170 * U * max(.01, s)
    glow = orb(int(240 * U), VIO, .5 * eo(prog(t, .1, 1)))
    _paste_any(L, glow, int(W / 2 - glow.width / 2), int(cy - glow.height / 2))
    if size > 3:
        L.alpha_composite(logo_img(size), (int(W / 2 - size / 2), int(cy - size / 2)))
    d = ImageDraw.Draw(L)
    p = eo(prog(t, .4, 1.0))
    text(d, (W / 2, cy + 150 * U + (1 - p) * 24 * U), "Mirage", font("dX", 96 * U), TXT, "mm", int(255 * p))
    p2 = eo(prog(t, .7, 1.3))
    text(d, (W / 2, cy + 222 * U + (1 - p2) * 14 * U), "Free, open source, touchpad-first. Windows 10 and 11.", font("r", 26 * U), SUB, "mm", int(255 * p2))
    p3 = back(prog(t, 1.0, 1.6))
    if p3 > 0:
        B = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        fb = font("s", 20 * U)
        b1 = "Download for Windows"; b2 = "No admin needed"
        w1 = d.textlength(b1, font=fb) + 60 * U; w2 = d.textlength(b2, font=fb) + 60 * U
        y = cy + 280 * U; x = W / 2 - (w1 + w2 + 18 * U) / 2
        shadow(B, (x, y, x + w1, y + 58 * U), int(29 * U), 120, int(24 * U), int(10 * U))
        paste_rr(B, (x, y), w1, 58 * U, int(29 * U), "acc", 255)
        db = ImageDraw.Draw(B)
        text(db, (x + w1 / 2, y + 29 * U), b1, fb, (255, 255, 255), "mm")
        x2 = x + w1 + 18 * U
        paste_rr(B, (x2, y), w2, 58 * U, int(29 * U), (255, 255, 255), 22)
        ring(B, (x2, y), w2, 58 * U, int(29 * U), (255, 255, 255), 70)
        db = ImageDraw.Draw(B)
        text(db, (x2 + w2 / 2, y + 29 * U), b2, fb, TXT, "mm")
        pc = clamp(p3)
        L.alpha_composite(transform(opacity(B, pc), lerp(.9, 1, min(p3, 1.05)), cx=W / 2, cy=y) if p3 != 1 else B)
    p4 = eo(prog(t, 1.5, 2.1))
    d = ImageDraw.Draw(L)
    text(d, (W / 2, cy + 400 * U), "hanialnaber.github.io/mirage-launcher", font("m", 22 * U), (190, 198, 255), "mm", int(255 * p4))
    return L

SCENES = [(0.0, 3.8, scene_intro), (3.3, 10.3, scene_corner), (9.8, 15.3, scene_swipe), (14.8, 20.8, scene_radial), (20.3, 26.8, scene_drop),
          (26.3, 32.0, scene_select), (31.5, 39.0, scene_agents), (38.5, 43.0, scene_grid), (42.5, DUR, scene_outro)]
XF = 0.5

def frame(t):
    bg = background(t)
    out = bg.copy()
    for i, (a, b, fn) in enumerate(SCENES):
        if not (a <= t < b): continue
        lt = t - a
        L = fn(bg, lt)
        pin = eio(prog(t, a, a + XF)) if i > 0 else 1.0
        pout = eio(prog(t, b - XF, b)) if i < len(SCENES) - 1 else 0.0
        if pin < 1:
            L = transform(opacity(L, pin), lerp(1.04, 1.0, pin), 0, 0, blur=(1 - pin) * 10 * U)
        if pout > 0:
            L = transform(opacity(L, 1 - pout), lerp(1.0, .96, pout), 0, 0, blur=pout * 10 * U)
        out.alpha_composite(L)
    return out.convert("RGB")

def init(w, h):
    global W, H, U
    W, H = w, h
    U = min(W / 1920, H / 1080) * 1.22 if W > H else W / 1080 * .92

def work(args):
    i, w, h = args
    if W != w or H != h: init(w, h)
    return frame(i / FPS).tobytes()

def main():
    w, h, out = int(sys.argv[1]), int(sys.argv[2]), sys.argv[3]
    init(w, h)
    if "--preview" in sys.argv:
        ts = [float(x) for x in sys.argv[sys.argv.index("--preview") + 1].split(",")]
        for tt in ts:
            frame(tt).save(f"{out}_{tt:05.1f}.png")
        return
    n = int(DUR * FPS)
    cmd = ["ffmpeg", "-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{w}x{h}", "-r", str(FPS), "-i", "-"]
    audio = os.path.join(HERE, "music.wav")
    if os.path.exists(audio): cmd += ["-i", audio, "-c:a", "aac", "-b:a", "160k", "-shortest"]
    cmd += ["-c:v", "libx264", "-preset", "slow", "-crf", "19", "-pix_fmt", "yuv420p", "-movflags", "+faststart", out]
    p = subprocess.Popen(cmd, stdin=subprocess.PIPE)
    with Pool(os.cpu_count(), initializer=init, initargs=(w, h)) as pool:
        for k, buf in enumerate(pool.imap(work, [(i, w, h) for i in range(n)], chunksize=4)):
            p.stdin.write(buf)
            if k % 150 == 0: print(f"{k}/{n}", flush=True)
    p.stdin.close(); p.wait()

if __name__ == "__main__":
    main()
