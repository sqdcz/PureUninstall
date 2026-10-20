#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
PureUninstall (纯净卸载) icon generator - v3 transparent background.
Transparent canvas + minimal teal recycle bin + one sweep arc + sparkles.
Drawn with Pillow at 2x supersampling, downscaled with LANCZOS.
Outputs: app.ico (multi-size, alpha) + PNG previews on white and dark.
"""
import math
import os
from PIL import Image, ImageDraw, ImageFilter, ImageChops

SS = 2
W = H = 1024
CW, CH = W * SS, H * SS
HERE = os.path.dirname(os.path.abspath(__file__))


def sc(v):
    return int(round(v * SS))


def box(x0, y0, x1, y1):
    return [sc(x0), sc(y0), sc(x1), sc(y1)]


def lerp(c1, c2, t):
    return tuple(int(round(a + (b - a) * t)) for a, b in zip(c1, c2))


def vgrad(w, h, c_top, c_bot):
    img = Image.new("RGBA", (w, h))
    d = ImageDraw.Draw(img)
    for y in range(h):
        d.line([(0, y), (w, y)], fill=lerp(c_top, c_bot, y / max(1, h - 1)))
    return img


def apply_grad(mask, c_top, c_bot):
    grad = vgrad(CW, CH, c_top, c_bot)
    out = Image.new("RGBA", (CW, CH), (0, 0, 0, 0))
    out.paste(grad, (0, 0), mask)
    return out


def star(cx, cy, R, n=4, inner=0.22):
    """4-point sparkle star with concave curved edges."""
    pts = []
    for i in range(n):
        ao = -math.pi / 2 + i * 2 * math.pi / n
        ai1 = ao + (2 * math.pi / n) * 0.30
        ai2 = ao + (2 * math.pi / n) * 0.70
        po = (cx + R * math.cos(ao), cy + R * math.sin(ao))
        p1 = (cx + R * inner * math.cos(ai1), cy + R * inner * math.sin(ai1))
        p2 = (cx + R * inner * math.cos(ai2), cy + R * inner * math.sin(ai2))
        for s in range(11):
            t = s / 10
            pts.append((
                (1 - t) ** 2 * p1[0] + 2 * (1 - t) * t * po[0] + t ** 2 * p2[0],
                (1 - t) ** 2 * p1[1] + 2 * (1 - t) * t * po[1] + t ** 2 * p2[1],
            ))
    return pts


# ---- transparent canvas (no background plate) ----
img = Image.new("RGBA", (CW, CH), (0, 0, 0, 0))

# ---- one sweep arc behind the bin ----
arc_layer = Image.new("RGBA", (CW, CH), (0, 0, 0, 0))
ad = ImageDraw.Draw(arc_layer)
cx, cy, r1 = 512, 584, 330
ad.arc(box(cx - r1, cy - r1, cx + r1, cy + r1), 298, 360, fill=(70, 195, 215, 180), width=sc(22))
ad.arc(box(cx - r1, cy - r1, cx + r1, cy + r1), 0, 22, fill=(70, 195, 215, 180), width=sc(22))
img.alpha_composite(arc_layer)

# ---- soft shadow under the bin ----
sh = Image.new("RGBA", (CW, CH), (0, 0, 0, 0))
ImageDraw.Draw(sh).ellipse(box(300, 724, 724, 804), fill=(6, 60, 82, 60))
sh = sh.filter(ImageFilter.GaussianBlur(sc(26)))
img.alpha_composite(sh)

# ---- bin opening rim (drawn before body so body covers its lower half) ----
interior = Image.new("RGBA", (CW, CH), (0, 0, 0, 0))
ImageDraw.Draw(interior).ellipse(box(352, 420, 672, 472), fill=(11, 108, 138, 255))
img.alpha_composite(interior)

# ---- bin body: tapered trapezoid with rounded bottom corners ----
body_mask = Image.new("L", (CW, CH), 0)
bm = ImageDraw.Draw(body_mask)
bm.polygon([(sc(352), sc(448)), (sc(672), sc(448)), (sc(698), sc(736)), (sc(326), sc(736))], fill=255)
bm.pieslice(box(326, 636, 426, 736), 90, 180, fill=255)
bm.pieslice(box(598, 636, 698, 736), 0, 90, fill=255)
img.alpha_composite(apply_grad(body_mask, (35, 201, 206), (12, 123, 160)))

# ---- ribs, clipped to body ----
ribs = Image.new("L", (CW, CH), 0)
rd = ImageDraw.Draw(ribs)
for x in (442, 512, 582):
    rd.rounded_rectangle(box(x - 7, 494, x + 7, 714), radius=sc(7), fill=255)
ribs = ImageChops.multiply(ribs, body_mask)
rib_layer = Image.new("RGBA", (CW, CH), (0, 0, 0, 0))
rib_layer.paste(Image.new("RGBA", (CW, CH), (255, 255, 255, 90)), (0, 0), ribs)
img.alpha_composite(rib_layer)

# ---- lid + handle, slightly tilted ----
lid_mask = Image.new("L", (CW, CH), 0)
ld = ImageDraw.Draw(lid_mask)
ld.rounded_rectangle(box(296, 364, 720, 420), radius=sc(26), fill=255)
ld.rounded_rectangle(box(482, 338, 546, 376), radius=sc(16), fill=255)
lid = apply_grad(lid_mask, (42, 210, 214), (16, 150, 182))
lid = lid.rotate(-4, center=(sc(508), sc(392)), resample=Image.BICUBIC)
img.alpha_composite(lid)

# ---- sparkles: one at the arc end, one small for balance ----
sp = Image.new("RGBA", (CW, CH), (0, 0, 0, 0))
spd = ImageDraw.Draw(sp)
spd.polygon([(sc(x), sc(y)) for x, y in star(726, 310, 50)], fill=(33, 196, 208, 255))
spd.polygon([(sc(x), sc(y)) for x, y in star(262, 350, 24)], fill=(33, 196, 208, 235))
img.alpha_composite(sp)

# ---- downscale & save ----
final = img.resize((W, H), Image.LANCZOS)
final.save(os.path.join(HERE, "pureuninstall_1024.png"))
final.resize((512, 512), Image.LANCZOS).save(os.path.join(HERE, "pureuninstall_512.png"))
final.resize((256, 256), Image.LANCZOS).save(os.path.join(HERE, "pureuninstall_256.png"))

# previews on light and dark backgrounds (what users actually see)
for name, color in (("preview_on_white.png", (255, 255, 255)), ("preview_on_dark.png", (31, 41, 55))):
    canvas = Image.new("RGBA", (512, 512), color + (255,))
    canvas.alpha_composite(final.resize((512, 512), Image.LANCZOS))
    canvas.convert("RGB").save(os.path.join(HERE, name))

sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
final.save(os.path.join(HERE, "app.ico"), format="ICO", sizes=sizes)

print("OK -> app.ico (transparent) + PNG previews")
