# Mirage app icon: the 3x3 gold dot mark from hani-alnaber.com on a navy tile.
import numpy as np
from PIL import Image, ImageDraw, ImageFilter
S = 1024; M = 100; R = 190
img = Image.new('RGBA', (S, S), (0, 0, 0, 0))
# navy gradient tile
y, x = np.mgrid[0:S, 0:S] / S
g = np.clip((x * .45 + y * .55), 0, 1)[..., None]
top = np.array([30, 46, 92]); bot = np.array([5, 8, 16])
rgb = (top * (1 - g) + bot * g)
# soft gold glow behind the mark
d = np.sqrt((x - .5) ** 2 + (y - .5) ** 2)
rgb = rgb * (1 - (np.exp(-(d / .26) ** 2) * .28))[..., None] + np.array([150, 105, 45]) * (np.exp(-(d / .26) ** 2) * .28)[..., None]
tile = Image.fromarray(np.clip(rgb, 0, 255).astype('uint8'), 'RGB').convert('RGBA')
mask = Image.new('L', (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle([M, M, S - M, S - M], R, fill=255)
img.paste(tile, (0, 0), mask)
dr = ImageDraw.Draw(img)
dr.rounded_rectangle([M + 2, M + 2, S - M - 2, S - M - 2], R - 2, outline=(244, 239, 230, 26), width=3)
# dots
cell = 112; dot = 74; start = S // 2 - cell
glow = Image.new('RGBA', (S, S), (0, 0, 0, 0)); gd = ImageDraw.Draw(glow)
for i in range(3):
    for j in range(3):
        k = i * 3 + j
        a = 255 if k % 2 == 0 else 95
        if k == 4: a = 255
        cx, cy = start + j * cell, start + i * cell
        box = [cx - dot // 2, cy - dot // 2, cx + dot // 2, cy + dot // 2]
        col = (255, 236, 190, a) if k == 4 else (242, 196, 107, a)
        gd.rounded_rectangle(box, 14, fill=(242, 196, 107, int(a * .9)))
        dr.rounded_rectangle(box, 14, fill=col)
glow = glow.filter(ImageFilter.GaussianBlur(26))
out = Image.alpha_composite(Image.alpha_composite(Image.new('RGBA', (S, S)), glow), img)
# re-draw dots crisp on top of glow
d2 = ImageDraw.Draw(out)
for i in range(3):
    for j in range(3):
        k = i * 3 + j; a = 255 if k % 2 == 0 else 95
        cx, cy = start + j * cell, start + i * cell
        box = [cx - dot // 2, cy - dot // 2, cx + dot // 2, cy + dot // 2]
        d2.rounded_rectangle(box, 14, fill=(255, 238, 200, 255) if k == 4 else ((242, 196, 107, 255) if a == 255 else (112, 84, 44, 255)))
# clip glow outside tile
final = Image.new('RGBA', (S, S), (0, 0, 0, 0)); final.paste(out, (0, 0), mask)
final.save('icon-source.png')
# tray icon: dots only, white-ish template for macOS, gold for Windows
for name, col in (('tray.png', (242, 196, 107)), ('tray-template.png', (0, 0, 0))):
    t = Image.new('RGBA', (64, 64), (0, 0, 0, 0)); td = ImageDraw.Draw(t)
    for i in range(3):
        for j in range(3):
            k = i * 3 + j; a = 255 if k % 2 == 0 else 110
            cx, cy = 14 + j * 18, 14 + i * 18
            td.rounded_rectangle([cx - 6, cy - 6, cx + 6, cy + 6], 2, fill=col + (a,))
    t.save(name)
print('ok')
