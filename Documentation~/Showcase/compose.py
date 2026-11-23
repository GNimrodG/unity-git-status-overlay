"""Trims the empty bottom of the showcase screenshots and composes the README hero image.

usage: python compose.py <screenshots dir> <images out dir>
"""
import os
import sys

from PIL import Image

PAD = 6
GAP = 12


def trim_bottom(img):
    """Cuts trailing rows that are one flat colour (empty window background), keeping a small margin."""
    rgb = img.convert("RGB")
    w, h = rgb.size
    px = rgb.load()
    background = px[w // 2, h - 1]
    last = h - 1
    while last > 0:
        row = {px[x, last] for x in range(0, w, 2)}
        if len(row) > 1 or background not in row:
            break
        last -= 1
    return rgb.crop((0, 0, w, min(h, last + 1 + PAD)))


def main():
    src, out = sys.argv[1], sys.argv[2]
    os.makedirs(out, exist_ok=True)
    shots = {}
    for name in sorted(os.listdir(src)):
        if name.endswith(".png"):
            shots[name] = trim_bottom(Image.open(os.path.join(src, name)))
            shots[name].save(os.path.join(out, name), optimize=True)

    left, right = shots["hierarchy.png"], shots["inspector-weapon.png"]
    height = max(left.height, right.height)
    hero = Image.new("RGB", (left.width + GAP + right.width, height), (25, 25, 25))
    hero.paste(left, (0, 0))
    hero.paste(right, (left.width + GAP, 0))
    hero.save(os.path.join(out, "showcase.png"), optimize=True)
    print("wrote", len(shots) + 1, "images to", out)


if __name__ == "__main__":
    main()
