"""Draws the FileWheel app icon (warm glass wheel) into Assets/FileWheel.ico."""
import math
import sys
from PIL import Image, ImageDraw, ImageFilter

S = 1024


def wedge(draw, cx, cy, r_in, r_out, a0, a1, fill):
    pts = []
    steps = 48
    for i in range(steps + 1):
        a = math.radians(a0 + (a1 - a0) * i / steps)
        pts.append((cx + r_out * math.cos(a), cy + r_out * math.sin(a)))
    for i in range(steps, -1, -1):
        a = math.radians(a0 + (a1 - a0) * i / steps)
        pts.append((cx + r_in * math.cos(a), cy + r_in * math.sin(a)))
    draw.polygon(pts, fill=fill)


def render():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    # Warm gradient disc
    grad = Image.new("RGBA", (S, S))
    gp = grad.load()
    for y in range(S):
        for x in range(S):
            t = (x * 0.35 + y * 0.65) / S
            r = int(250 - 18 * t)
            g = int(178 - 96 * t)
            b = int(64 + 30 * t)
            gp[x, y] = (r, g, b, 255)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).ellipse((40, 40, S - 40, S - 40), fill=255)
    img.paste(grad, (0, 0), mask)

    # Cream wedges with gaps (drawn large then the gaps are cut out)
    layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    cx = cy = S / 2
    n = 6
    for i in range(n):
        span = 360 / n
        a0 = -90 - span / 2 + i * span + 5
        a1 = a0 + span - 10
        fill = (255, 236, 214, 235) if i != 1 else (255, 255, 255, 0)
        wedge(d, cx, cy, 190, 420, a0, a1, fill)
    # Highlighted wedge in deep orange
    span = 360 / n
    a0 = -90 - span / 2 + 1 * span + 5
    wedge(d, cx, cy, 190, 420, a0, a0 + span - 10, (232, 74, 34, 255))
    layer = layer.filter(ImageFilter.GaussianBlur(1.5))
    img.alpha_composite(layer)

    # Center pill
    d = ImageDraw.Draw(img)
    d.ellipse((cx - 120, cy - 120, cx + 120, cy + 120), fill=(255, 245, 235, 120))
    d.rounded_rectangle((cx - 92, cy - 40, cx + 92, cy + 40), radius=40, fill=(255, 226, 196, 255))
    # Rim
    d.ellipse((44, 44, S - 44, S - 44), outline=(255, 255, 255, 150), width=10)
    return img


if __name__ == "__main__":
    out = sys.argv[1]
    big = render()
    sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]
    big.save(out, format="ICO", sizes=sizes)
    big.resize((256, 256), Image.LANCZOS).save(out.replace(".ico", ".png"))
