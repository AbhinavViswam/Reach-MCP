"""
Reach's Windows icon, the same design as Reach for Android's launcher icon (res/drawable/ic_launcher_fg.xml on
res/values reach_icon_bg): a navy rounded square, a teal ring open at the top right, a teal dot reaching out of the
gap, a white dot in the middle. The Android drawing is on a 108-unit canvas; the visible part is 18..90.
"""
import sys
from PIL import Image, ImageDraw

out = sys.argv[1]
NAVY = (0x0E, 0x1A, 0x2B, 255)
TEAL = (0x5E, 0xE6, 0xC8, 255)
WHITE = (255, 255, 255, 255)


def draw(size):
    s = size * 8  # draw big, then shrink: smooth edges
    im = Image.new('RGBA', (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    pad = max(1, s // 40)
    d.rounded_rectangle([pad, pad, s - pad, s - pad], radius=int(s * 0.22), fill=NAVY)
    # Android units (visible 18..90, 72 wide) → pixels.
    k = s / 72.0
    def P(x, y): return ((x - 18) * k, (y - 18) * k)
    cx, cy = P(54, 54)
    r = 20 * k
    w = 6 * k
    # Ring: from 0° (3 o'clock) clockwise to 315°, leaving the gap at the top right (Android: M68,40 A20,20 0 1,0 72,54).
    d.arc([cx - r - w / 2, cy - r - w / 2, cx + r + w / 2, cy + r + w / 2], start=0, end=315, fill=TEAL, width=int(w))
    # Round caps at both ends of the ring.
    for ang in (0, 315):
        import math
        a = math.radians(ang)
        ex, ey = cx + r * math.cos(a), cy + r * math.sin(a)
        d.ellipse([ex - w / 2, ey - w / 2, ex + w / 2, ey + w / 2], fill=TEAL)
    dx, dy = P(74, 34)
    d.ellipse([dx - 7 * k, dy - 7 * k, dx + 7 * k, dy + 7 * k], fill=TEAL)
    d.ellipse([cx - 5 * k, cy - 5 * k, cx + 5 * k, cy + 5 * k], fill=WHITE)
    return im.resize((size, size), Image.LANCZOS)


sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
imgs = [draw(n) for n in sizes]
imgs[-1].save(out, format='ICO', sizes=[(n, n) for n in sizes], append_images=imgs[:-1])
imgs[-1].save(out.replace('.ico', '-256.png'))
imgs[sizes.index(32)].resize((128, 128), Image.NEAREST).save(out.replace('.ico', '-32x4.png'))
print('ok', out)
