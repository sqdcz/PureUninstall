#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Generate MSIX logo assets from the master icon PNG."""
import os
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "..", "..", "..", "design", "icons", "pureuninstall_1024.png")

def make(name, size, pad_ratio=0.12, bg=(255, 255, 255)):
    icon = Image.open(SRC).convert("RGBA")
    canvas = Image.new("RGBA", size, bg + (255,))
    inner = int(min(size) * (1 - pad_ratio * 2))
    icon_resized = icon.resize((inner, inner), Image.LANCZOS)
    canvas.alpha_composite(icon_resized, ((size[0] - inner) // 2, (size[1] - inner) // 2))
    canvas.convert("RGB").save(os.path.join(HERE, name))
    print(f"{name} {size[0]}x{size[1]}")

def make_wide(name, size):
    icon = Image.open(SRC).convert("RGBA")
    canvas = Image.new("RGBA", size, (240, 249, 251, 255))
    inner = int(size[1] * 0.76)
    icon_resized = icon.resize((inner, inner), Image.LANCZOS)
    canvas.alpha_composite(icon_resized, (int(size[0] * 0.30), (size[1] - inner) // 2))
    canvas.convert("RGB").save(os.path.join(HERE, name))
    print(f"{name} {size[0]}x{size[1]}")

make("Square44x44Logo.png", (44, 44))
make("Square71x71Logo.png", (71, 71))
make("Square150x150Logo.png", (150, 150))
make("Square310x310Logo.png", (310, 310))
make("StoreLogo.png", (50, 50))
make_wide("Wide310x150Logo.png", (310, 150))
print("ALL ASSETS DONE")
