#!/usr/bin/env python3
"""Mirage launch trailer: fast cuts on the beat, camera moves on the real UI,
kinetic type and transitions, with an original synthesized beat.

Input: the clean stills render.mjs writes (win-*.png / mac-*.png).
    python3 trailer.py --os win --stills DIR --out trailer-win.mp4 [--sample]
"""
import argparse, math, os, shutil, subprocess, sys, wave
from multiprocessing import Pool

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

W, H, FPS = 1920, 1080, 30
BPM = 120
BEAT = 60 / BPM
HERE = os.path.dirname(os.path.abspath(__file__))
FONTS = os.path.join(HERE, '..', '..', 'ui', 'fonts')

NIGHT = (5, 8, 16)
NAVY = (14, 24, 48)
GOLD = (242, 196, 107)
IVORY = (244, 239, 230)
MUTE = (150, 160, 178)

# ---------------------------------------------------------------- helpers

def clamp(x, a=0.0, b=1.0):
    return a if x < a else b if x > b else x

def lerp(a, b, t):
    return a + (b - a) * t

def ease_out(t):  # expo out
    t = clamp(t)
    return 1 - 2 ** (-10 * t) if t < 1 else 1.0

def ease_in(t):
    t = clamp(t)
    return 2 ** (10 * t - 10) if t > 0 else 0.0

def ease_io(t):
    t = clamp(t)
    return 0.5 - 0.5 * math.cos(math.pi * t)

def back_out(t, s=1.6):
    t = clamp(t) - 1
    return t * t * ((s + 1) * t + s) + 1

_fc = {}
def font(size, wght=800, wdth=125, mono=False):
    key = (size, wght, wdth, mono)
    if key not in _fc:
        f = ImageFont.truetype(os.path.join(FONTS, 'JetBrainsMono.ttf' if mono else 'Archivo.ttf'), size)
        f.set_variation_by_axes([wght] if mono else [wght, wdth])
        _fc[key] = f
    return _fc[key]

def persp_coeffs(dst, src):
    """Coefficients mapping output points (dst) to input points (src)."""
    A, B = [], []
    for (x, y), (u, v) in zip(dst, src):
        A.append([x, y, 1, 0, 0, 0, -u * x, -u * y]); B.append(u)
        A.append([0, 0, 0, x, y, 1, -v * x, -v * y]); B.append(v)
    return np.linalg.solve(np.array(A, float), np.array(B, float)).tolist()

def project(w, h, rx, ry, rz, z, cx, cy, f=2200):
    """Corners of a w x h card rotated in 3D (degrees), pushed back by z, seen from f."""
    rx, ry, rz = map(math.radians, (rx, ry, rz))
    pts = []
    for x, y in ((-w / 2, -h / 2), (w / 2, -h / 2), (w / 2, h / 2), (-w / 2, h / 2)):
        x, y = x * math.cos(rz) - y * math.sin(rz), x * math.sin(rz) + y * math.cos(rz)
        y, zz = y * math.cos(rx), y * math.sin(rx)
        x, zz = x * math.cos(ry) + zz * math.sin(ry), -x * math.sin(ry) + zz * math.cos(ry)
        zz += z
        s = f / (f + zz)
        pts.append((cx + x * s, cy + y * s))
    return pts

def rounded_rgba(img, r):
    m = Image.new('L', img.size, 0)
    ImageDraw.Draw(m).rounded_rectangle((0, 0, img.size[0] - 1, img.size[1] - 1), r, fill=255)
    out = img.convert('RGBA'); out.putalpha(m)
    d = ImageDraw.Draw(out)
    d.rounded_rectangle((0, 0, img.size[0] - 1, img.size[1] - 1), r, outline=(255, 255, 255, 46), width=3)
    return out

def card(canvas, src, quad, shadow=True, alpha=1.0):
    """Warp an RGBA image onto quad (tl, tr, br, bl) of the canvas, with a soft shadow."""
    sw, sh = src.size
    c = persp_coeffs(quad, [(0, 0), (sw, 0), (sw, sh), (0, sh)])
    layer = src.transform((W, H), Image.PERSPECTIVE, c, Image.BICUBIC)
    a = layer.getchannel('A')
    if alpha < 1:
        a = a.point(lambda v: int(v * alpha))
    if shadow:
        s = a.resize((W // 4, H // 4)).filter(ImageFilter.GaussianBlur(14)).resize((W, H))
        s = Image.eval(s, lambda v: int(v * 0.75 * alpha))
        dark = Image.new('RGB', (W, H), (0, 0, 0))
        canvas.paste(dark, (0, 34), s)
    canvas.paste(layer.convert('RGB'), (0, 0), a)

def camera(img, cx, cy, zoom, rot=0.0):
    """A camera framing a full-screen still: center (in image px), zoom, roll."""
    iw, ih = img.size
    vw, vh = W / zoom, H / zoom
    src = []
    for x, y in ((-vw / 2, -vh / 2), (vw / 2, -vh / 2), (vw / 2, vh / 2), (-vw / 2, vh / 2)):
        r = math.radians(rot)
        src.append((cx + x * math.cos(r) - y * math.sin(r), cy + x * math.sin(r) + y * math.cos(r)))
    c = persp_coeffs([(0, 0), (W, 0), (W, H), (0, H)], src)
    return img.transform((W, H), Image.PERSPECTIVE, c, Image.BICUBIC)

def smear(img, amount, vertical=False):
    """Motion blur along the move: amount 0..1."""
    if amount <= 0.02:
        return img
    k = int(lerp(1, 40, amount))
    if vertical:
        b = img.resize((W, max(2, H // k)), Image.BILINEAR).resize((W, H), Image.BILINEAR)
    else:
        b = img.resize((max(2, W // k), H), Image.BILINEAR).resize((W, H), Image.BILINEAR)
    return Image.blend(img, b, clamp(amount * 1.4))

def flash(img, amount, color=(255, 236, 200)):
    if amount <= 0:
        return img
    return Image.blend(img, Image.new('RGB', (W, H), color), clamp(amount))

def vignette():
    y, x = np.mgrid[0:H, 0:W]
    d = np.sqrt(((x - W / 2) / (W * 0.62)) ** 2 + ((y - H / 2) / (H * 0.62)) ** 2)
    return np.clip(1.15 - d * 0.75, 0.25, 1.0)[..., None].astype(np.float32)

# ---------------------------------------------------------------- assets

A = {}

def load(os_, stills, walls=''):
    A['os'] = os_
    wp = os.path.join(walls, 'connected-world.jpg') if walls else ''
    if wp and os.path.exists(wp):
        A['globe'] = Image.open(wp).convert('RGB').resize((2880, 1620), Image.LANCZOS)
    for name in ('desktop', 'palette', 'assistant', 'voice', 'overview', 'wallpaper', 'ambient'):
        A[name] = Image.open(os.path.join(stills, f'{os_}-{name}.png')).convert('RGB')
    A['vig'] = vignette()
    bg = A['ambient'].resize((W // 8, H // 8)).filter(ImageFilter.GaussianBlur(6)).resize((W, H), Image.BICUBIC)
    arr = np.asarray(bg, np.float32) * 0.42 * A['vig']
    A['bg'] = Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8))
    for k in ('desktop', 'palette', 'assistant', 'overview'):
        A[k + '_card'] = rounded_rgba(A[k], 26)

def backdrop(t, drift=0.0):
    """Dark navy stage with a slow drift, for 3D card shots."""
    z = 1.08 + 0.04 * math.sin(t * 0.7 + drift)
    return camera(A['bg'], W / 2 + 40 * math.sin(t * 0.5 + drift), H / 2, z)

def grade(img):
    arr = np.asarray(img, np.float32) * A['vig']
    return Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8))

# ---------------------------------------------------------------- type

def words(canvas, t, t0, spec, x, y, size, align='left', stagger=0.09, dur=0.45, gap=0.28,
          wdth=125, wght=800, out=None):
    """Kinetic words: each slides up out of a mask and settles, one per stagger.
    spec: list of (word, color). out: (t_out) words fly up and away."""
    d = ImageDraw.Draw(canvas)
    f = font(size, wght, wdth)
    widths = [d.textlength(w, font=f) for w, _ in spec]
    space = size * gap
    total = sum(widths) + space * (len(spec) - 1)
    cx = x - total / 2 if align == 'center' else x - total if align == 'right' else x
    layer = Image.new('L', (W, H), 0)
    color_layers = []
    for i, ((w, col), ww) in enumerate(zip(spec, widths)):
        p = ease_out((t - t0 - i * stagger) / dur)
        if p <= 0:
            cx += ww + space; continue
        dy = (1 - p) * size * 0.9
        a = p
        if out is not None:
            q = ease_in((t - out - i * 0.04) / 0.3)
            dy -= q * size * 1.2; a *= 1 - q
        m = Image.new('L', (W, H), 0)
        ImageDraw.Draw(m).text((cx, y + dy), w, font=f, fill=int(255 * a), anchor='ls')
        # mask: hide below the baseline so words rise out of a slot
        clip = Image.new('L', (W, H), 0)
        ImageDraw.Draw(clip).rectangle((0, y - size * 1.2 - (size if out is not None else 0), W, y + size * 0.28), fill=255)
        m = Image.fromarray(np.minimum(np.asarray(m), np.asarray(clip)))
        color_layers.append((m, col))
        cx += ww + space
    for m, col in color_layers:
        canvas.paste(Image.new('RGB', (W, H), col), (0, 0), m)

def label(canvas, t, t0, text, x, y, size=26, color=GOLD, out=None):
    """Small mono label that types on."""
    n = int(clamp((t - t0) / 0.35) * len(text))
    if n <= 0:
        return
    a = 1.0
    if out is not None:
        a = 1 - clamp((t - out) / 0.2)
    d = ImageDraw.Draw(canvas)
    col = tuple(int(c * a) for c in color)
    d.text((x, y), text[:n].upper(), font=font(size, 500, mono=True), fill=col)
    if n < len(text) and int(t * 12) % 2 == 0:
        bx = x + d.textlength(text[:n].upper(), font=font(size, 500, mono=True)) + 4
        d.rectangle((bx, y + 4, bx + size * 0.55, y + size + 2), fill=col)

def mark(canvas, t, t0, cx, cy, cell, gap):
    """Mirage's 3 x 3 mark, cells popping in one by one."""
    d = ImageDraw.Draw(canvas)
    order = [4, 0, 2, 8, 6, 1, 5, 7, 3]
    for k, i in enumerate(order):
        p = back_out((t - t0 - k * 0.045) / 0.38)
        if p <= 0:
            continue
        r, c = divmod(i, 3)
        s = cell * max(0, p)
        x = cx + (c - 1) * (cell + gap); y = cy + (r - 1) * (cell + gap)
        op = 1.0 if i % 2 == 0 else 0.38
        col = tuple(int(lerp(NIGHT[j], GOLD[j], op)) for j in range(3))
        d.rounded_rectangle((x - s / 2, y - s / 2, x + s / 2, y + s / 2), max(2, s * 0.22), fill=col)

def glow(canvas, cx, cy, r, amount):
    if amount <= 0:
        return canvas
    g = Image.new('L', (W // 4, H // 4), 0)
    ImageDraw.Draw(g).ellipse(((cx - r) / 4, (cy - r) / 4, (cx + r) / 4, (cy + r) / 4), fill=int(255 * amount))
    g = g.filter(ImageFilter.GaussianBlur(r / 10)).resize((W, H), Image.BICUBIC)
    arr = np.asarray(canvas, np.float32) + np.asarray(g, np.float32)[..., None] / 255 * np.array(GOLD, np.float32) * 0.55
    return Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8))

# ---------------------------------------------------------------- shots
# Each shot draws the frame at time t (seconds, global). Times are in beats.

def b(n):
    return n * BEAT

def s_open(t):
    """b0-b6: mark pops, MIRAGE flies in, 'Your desktop. Upgraded.'"""
    c = Image.new('RGB', (W, H), NIGHT)
    rise = ease_io((t - b(2)) / 0.5)
    mark(c, t, 0.05, W / 2, H / 2 - 30 - rise * 150, 30, 12)
    c = glow(c, W / 2, H / 2 - rise * 150, 260, 0.5 * (1 - clamp(t / 1.4)) + 0.12)
    if t >= b(2):
        # MIRAGE: letters scale down into place
        p = ease_out((t - b(2)) / 0.6)
        size = int(lerp(300, 168, p))
        tr = lerp(60, 10, p)
        f = font(size, 800, 125)
        d = ImageDraw.Draw(c)
        txt = 'MIRAGE'
        ws = [d.textlength(ch, font=f) for ch in txt]
        x = W / 2 - (sum(ws) + tr * (len(txt) - 1)) / 2
        out = ease_in((t - b(4)) / 0.3)
        for i, (ch, w) in enumerate(zip(txt, ws)):
            q = ease_out((t - b(2) - i * 0.04) / 0.4)
            col = tuple(int(v * q * (1 - out)) for v in IVORY)
            d.text((x, H / 2 + 60 - out * 120), ch, font=f, fill=col, anchor='ls')
            x += w + tr
    if t >= b(4):
        c2 = Image.new('RGB', (W, H), NIGHT)
        words(c2, t, b(4), [('Your', IVORY), ('desktop.', IVORY)], W / 2, H / 2 - 20, 132, 'center', dur=0.35)
        words(c2, t, b(5), [('Upgraded.', GOLD)], W / 2, H / 2 + 140, 132, 'center', dur=0.35)
        p = clamp((t - b(4)) / 0.15)
        c = Image.blend(c, c2, p)
        c = flash(c, 0.35 * (1 - clamp((t - b(5)) / 0.25)) if t >= b(5) else 0)
    return c

def s_desktop(t):
    """b6-b9: whip in on a tilted desktop card, settle, push in."""
    lt = t - b(6)
    c = backdrop(t)
    p = ease_out(lt / 0.55)
    ry = lerp(-38, -9, p) + lt * 2
    rx = lerp(14, 6, p)
    x = lerp(W * 1.4, W / 2 + 40, p)
    z = lerp(600, -180, ease_io(lt / b(3)))
    quad = project(1540, 866, rx, ry, -1.5, z, x, H / 2 + 10)
    card(c, A['desktop_card'], quad)
    c = smear(c, (1 - p) * 0.9)
    words(c, t, b(7), [('A', IVORY), ('live', IVORY), ('top', IVORY), ('bar.', GOLD)], 120, H - 120, 88, out=b(8.6))
    return c

def s_bar(t):
    """b9-b11: the top bar's live stats, huge, gliding past in 3D."""
    lt = t - b(9)
    c = backdrop(t, 1)
    p = ease_io(lt / b(2))
    q = ease_out(lt / 0.45)
    if 'bar_card' not in A:
        x0 = 760 if A['os'] == 'win' else 1500
        strip = A['desktop'].crop((x0, 0, W, 36))
        A['bar_card'] = rounded_rgba(strip.resize((strip.width * 3, 108), Image.LANCZOS), 30)
    sw, sh = A['bar_card'].size
    h = 270
    w = sw * h / sh
    quad = project(w, h, lerp(16, 6, q), lerp(-18, -8, p), lerp(-3, -1, q), lerp(900, 150, q),
                   lerp(W / 2 + w * 0.42, W / 2 - w * 0.32, p), H / 2 - 30)
    card(c, A['bar_card'], quad)
    c = smear(c, clamp(1 - lt / 0.3) * 0.7)
    c = flash(c, 0.5 * (1 - clamp(lt / 0.18)))
    label(c, t, b(9.5), 'cpu · memory · gpu · network, live', 120, H - 160, 30)
    return grade(c)

def glass_wipe(a, b_, p, band=380):
    """A frosted glass band sweeps right to left, revealing b_ behind it."""
    edge = int(lerp(W + band, -band, ease_io(p)))
    out = a.copy()
    if edge < W:
        out.paste(b_.crop((max(edge, 0), 0, W, H)), (max(edge, 0), 0))
    x0, x1 = max(0, edge - band // 2), min(W, edge + band // 2)
    if x1 > x0:
        region = out.crop((x0, 0, x1, H)).filter(ImageFilter.GaussianBlur(28))
        arr = np.asarray(region, np.float32) * 0.85 + np.array([28, 34, 52], np.float32)
        region = Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8))
        out.paste(region, (x0, 0))
        d = ImageDraw.Draw(out)
        d.line((edge, 0, edge, H), fill=(255, 226, 170), width=2)
    return out

def s_palette_shot(t):
    lt = t - b(11)
    img = A['palette']
    z = lerp(1.25, 1.7, ease_io(lt / b(2.5)))
    cx, cy = 960, 470
    c = camera(img, lerp(cx - 120, cx + 60, ease_io(lt / b(2.5))), cy, z, lerp(1.2, -0.6, lt / b(2.5)))
    return grade(c)

def s_palette(t):
    """b11-b13.5: glass wipe from the bar to the command bar, push in."""
    lt = t - b(11)
    c = s_palette_shot(t)
    if lt < 0.45:
        c = glass_wipe(s_bar(b(11) - 0.01), c, lt / 0.45)
    words(c, t, b(11.8), [('One', IVORY), ('bar', IVORY), ('for', IVORY), ('everything.', GOLD)], 120, H - 120, 84, out=b(13.1))
    return c

def s_ask(t):
    """b13.5-b16: 'Ask.' slams, then 'Mirage does it.'"""
    lt = t - b(13.5)
    base = camera(A['assistant'], W / 2, H / 2, 1.1 + lt * 0.05).filter(ImageFilter.GaussianBlur(18))
    base = Image.fromarray((np.asarray(base, np.float32) * 0.28).astype(np.uint8))
    c = base
    p = back_out(lt / 0.32)
    size = int(lerp(420, 260, clamp(p)))
    d = ImageDraw.Draw(c)
    out = ease_in((t - b(15)) / 0.25)
    d.text((W / 2, H / 2 + 60 - out * 300), 'Ask.', font=font(size, 900, 125), fill=tuple(int(v * (1 - out)) for v in IVORY), anchor='ms')
    words(c, t, b(15), [('Mirage', GOLD), ('does', IVORY), ('it.', IVORY)], W / 2, H / 2 + 60, 150, 'center', dur=0.3)
    c = flash(c, 0.6 * (1 - clamp(lt / 0.15)))
    return c

def s_assistant(t):
    """b16-b19: iris opens from the orb into the assistant, push toward the steps."""
    lt = t - b(16)
    img = A['assistant']
    p = ease_io(lt / b(3))
    tx, ty = 1180, 470
    c = camera(img, lerp(W / 2, tx, p), lerp(H / 2, ty, p), lerp(1.0, 1.9, p), lerp(-2, 0, p))
    c = grade(c)
    r = lerp(0, 2300, ease_out(lt / 0.6))
    if r < 2300:
        m = Image.new('L', (W, H), 0)
        ox, oy = W / 2, H / 2
        ImageDraw.Draw(m).ellipse((ox - r, oy - r, ox + r, oy + r), fill=255)
        prev = s_ask(b(16) - 0.01)
        prev.paste(c, (0, 0), m)
        ImageDraw.Draw(prev).ellipse((ox - r, oy - r, ox + r, oy + r), outline=GOLD, width=4)
        c = prev
    label(c, t, b(16.8), 'shows every step · asks before it acts', 120, H - 110, 30, out=b(18.6))
    return c

def s_overview(t):
    """b19-b21: flash, overview as a tilted plane flying under the camera."""
    lt = t - b(19)
    c = backdrop(t, 2)
    p = ease_out(lt / 0.7)
    quad = project(1700, 956, lerp(48, 20, p), lerp(0, -6, lt / b(2)), lerp(-8, 0, p), lerp(900, 50, p), W / 2, lerp(H * 0.85, H / 2 + 40, p))
    card(c, A['overview_card'], quad)
    c = flash(c, 0.7 * (1 - clamp(lt / 0.2)))
    words(c, t, b(19.6), [('Every', IVORY), ('window.', IVORY)], 120, 190, 96, out=b(20.7))
    words(c, t, b(20), [('One', GOLD), ('glance.', GOLD)], 120, 300, 96, out=b(20.7))
    return c

def s_wallpaper(t):
    """b21-b23: vertical whip into the globe wallpaper, slow roll."""
    lt = t - b(21)
    p = ease_out(lt / 0.5)
    if 'globe' in A:
        img = A['globe']
        c = camera(img, lerp(1500, 1560, lt), lerp(-500, 830, p), lerp(1.25, 1.05, ease_io(lt / b(2))), lerp(6, 1.5, p))
    else:
        img = A['ambient']
        c = camera(img, lerp(1250, 1300, lt), lerp(-300, 390, p), lerp(1.75, 1.55, ease_io(lt / b(2))), lerp(6, 2, p))
    c = smear(c, (1 - p) * 0.9, vertical=True)
    c = grade(c)
    words(c, t, b(21.6), [('Original', IVORY), ('4K', GOLD), ('wallpapers.', IVORY)], W - 120, H - 120, 84, 'right', out=b(22.7))
    return c

def s_ambient(t):
    """b23-b24.5: hard cut onto the ambient clock, push in."""
    lt = t - b(23)
    c = camera(A['ambient'], lerp(560, 600, lt), lerp(330, 300, lt), lerp(1.5, 1.8, ease_io(lt / b(1.5))))
    c = flash(c, 0.45 * (1 - clamp(lt / 0.15)))
    return grade(c)

def s_end(t):
    """b24.5-end: mark, name and the offer."""
    lt = t - b(24.5)
    c = Image.new('RGB', (W, H), NIGHT)
    mark(c, t, b(24.5), W / 2, H / 2 - 190, 26, 10)
    c = glow(c, W / 2, H / 2 - 40, 520, 0.18 + 0.1 * math.sin(lt * 3))
    words(c, t, b(25), [('Mirage', IVORY)], W / 2, H / 2 + 60, 150, 'center', dur=0.4)
    words(c, t, b(25.6), [('Free', GOLD), ('for', IVORY), ('Windows', IVORY), ('and', IVORY), ('macOS.', IVORY)], W / 2, H / 2 + 160, 54, 'center', wght=600, wdth=110)
    label(c, t, b(26.4), 'hani-alnaber.com/osshell', W / 2 - 230, H / 2 + 230, 30, MUTE)
    if lt < 0.4:
        c = Image.blend(s_ambient(b(24.5) - 0.01), c, ease_io(lt / 0.4))
    fade = clamp((t - (A['dur'] - 0.6)) / 0.6)
    if fade > 0:
        c = Image.blend(c, Image.new('RGB', (W, H), (0, 0, 0)), fade)
    return c

SAMPLE = [
    (0, s_open), (b(6), s_desktop), (b(9), s_bar), (b(11), s_palette), (b(13.5), s_ask),
    (b(16), s_assistant), (b(19), s_overview), (b(21), s_wallpaper), (b(23), s_ambient), (b(24.5), s_end),
]

def frame(i):
    t = i / FPS
    fn = SAMPLE[0][1]
    for st, f in SAMPLE:
        if t >= st:
            fn = f
    return fn(t)

def work(args):
    i, d = args
    frame(i).save(os.path.join(d, f'{i:05d}.jpg'), quality=94)
    return i

def init(os_, stills, dur, walls=''):
    load(os_, stills, walls); A['dur'] = dur

# ---------------------------------------------------------------- audio

def audio(path, dur, hits):
    sr = 44100
    n = int(dur * sr)
    out = np.zeros(n, np.float32)
    rng = np.random.default_rng(7)

    def add(sig, at):
        i = int(at * sr)
        if i >= n:
            return
        j = min(n, i + len(sig))
        out[i:j] += sig[: j - i]

    def env(len_s, attack=0.002, decay=0.2):
        t = np.arange(int(len_s * sr)) / sr
        return np.minimum(t / attack, 1) * np.exp(-t / decay)

    t1 = np.arange(int(0.45 * sr)) / sr
    kick = np.sin(2 * np.pi * np.cumsum(45 + 110 * np.exp(-t1 * 28)) / sr) * env(0.45, 0.001, 0.16) * 0.9
    hat_n = rng.standard_normal(int(0.06 * sr)).astype(np.float32)
    hat = np.diff(hat_n, prepend=0) * env(0.06, 0.0005, 0.015) * 0.22
    clap_n = rng.standard_normal(int(0.2 * sr)).astype(np.float32)
    clap = np.convolve(clap_n, np.ones(6) / 6, 'same') * env(0.2, 0.001, 0.05) * 0.35

    beats = int(dur / BEAT)
    for k in range(beats):
        at = k * BEAT
        if at >= b(24.5) and at > dur - 1.2:
            continue
        add(kick, at)
        add(hat, at + BEAT / 2)
        if k % 2 == 1:
            add(clap, at)
        add(hat * 0.5, at + BEAT * 0.75)

    # bass: a minor line, eighth notes, gated
    notes = [41.2, 41.2, 49.0, 36.7]  # E1 E1 G1 D1
    for k in range(beats * 2):
        at = k * BEAT / 2
        f0 = notes[(k // 8) % 4] * 2
        tt = np.arange(int(BEAT / 2 * sr)) / sr
        saw = 2 * ((tt * f0) % 1) - 1
        add((0.6 * np.sin(2 * np.pi * f0 * tt) + 0.18 * saw) * env(BEAT / 2, 0.004, 0.12) * 0.32, at)

    # pad: slow chords, detuned sines
    chords = [[164.8, 196.0, 246.9], [130.8, 164.8, 196.0], [146.8, 174.6, 220.0], [123.5, 146.8, 185.0]]
    bar = BEAT * 4
    for k in range(int(dur / bar) + 1):
        tt = np.arange(int(bar * sr)) / sr
        sig = sum(np.sin(2 * np.pi * f * tt) + np.sin(2 * np.pi * f * 1.004 * tt) for f in chords[k % 4])
        e = np.minimum(tt / 0.3, 1) * np.minimum((bar - tt) / 0.3, 1)
        add(sig * e * 0.035, k * bar)

    # transitions: whooshes and impacts
    for kind, at in hits:
        if kind == 'whoosh':
            L = 0.45
            noise = rng.standard_normal(int(L * sr)).astype(np.float32)
            tt = np.arange(len(noise)) / sr
            k = np.convolve(noise, np.ones(24) / 24, 'same')
            e = np.sin(np.pi * np.clip(tt / L, 0, 1)) ** 2
            add(k * e * 0.9, at - L * 0.6)
        elif kind == 'impact':
            L = 1.2
            tt = np.arange(int(L * sr)) / sr
            boom = np.sin(2 * np.pi * np.cumsum(32 + 60 * np.exp(-tt * 10)) / sr) * np.exp(-tt / 0.45)
            noise = rng.standard_normal(len(tt)).astype(np.float32) * np.exp(-tt / 0.08) * 0.3
            add((boom * 0.9 + noise), at)
        elif kind == 'riser':
            L = 1.5
            tt = np.arange(int(L * sr)) / sr
            f = 200 * 2 ** (tt / L * 3)
            add(np.sin(2 * np.pi * np.cumsum(f) / sr) * (tt / L) ** 2 * 0.12, at - L)

    # gentle bus compression and limiting
    out = np.tanh(out * 1.3)
    tail = int(0.8 * sr)
    out[-tail:] *= np.linspace(1, 0, tail)
    out /= max(1e-6, np.abs(out).max()) / 0.79  # peak -2 dBFS, headroom for AAC
    st = np.stack([out, out], 1)
    with wave.open(path, 'wb') as w:
        w.setnchannels(2); w.setsampwidth(2); w.setframerate(sr)
        w.writeframes((st * 32767).astype(np.int16).tobytes())

SAMPLE_HITS = [('impact', 0.05), ('impact', b(4)), ('whoosh', b(6)), ('impact', b(9)), ('whoosh', b(11)),
               ('impact', b(13.5)), ('whoosh', b(16)), ('impact', b(19)), ('whoosh', b(21)),
               ('impact', b(23)), ('riser', b(24.5)), ('impact', b(24.5))]

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--os', default='win')
    ap.add_argument('--stills', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--frames', default='')
    ap.add_argument('--walls', default='', help='folder with the 4K wallpapers (tools/wallpapers.py)')
    ap.add_argument('--only', type=float, nargs='*', help='render single stills at these times')
    a = ap.parse_args()
    dur = b(28)
    if a.only:
        init(a.os, a.stills, dur, a.walls)
        for t in a.only:
            frame(int(t * FPS)).save(f'{a.out}-{t:05.2f}.jpg', quality=88)
        return
    d = a.frames or a.out + '.frames'
    shutil.rmtree(d, ignore_errors=True); os.makedirs(d)
    n = int(dur * FPS)
    with Pool(os.cpu_count(), initializer=init, initargs=(a.os, a.stills, dur, a.walls)) as pool:
        for k, _ in enumerate(pool.imap_unordered(work, [(i, d) for i in range(n)], chunksize=4)):
            if k % 60 == 0:
                print(f'{k}/{n}', flush=True)
    wav = a.out + '.wav'
    audio(wav, dur, SAMPLE_HITS)
    subprocess.run(['ffmpeg', '-y', '-loglevel', 'error', '-framerate', str(FPS), '-i', os.path.join(d, '%05d.jpg'), '-i', wav,
                    '-c:v', 'libx264', '-preset', 'slow', '-crf', '19', '-pix_fmt', 'yuv420p', '-af', 'alimiter=limit=0.7:level=disabled', '-c:a', 'aac', '-b:a', '192k',
                    '-shortest', '-movflags', '+faststart', a.out], check=True)
    shutil.rmtree(d, ignore_errors=True)
    print('wrote', a.out)

if __name__ == '__main__':
    main()
