"""Draws the PC Health Monitor icon: a drive with a heartbeat line on a blue tile (T-68).

    python3 tools/icon/make_icon.py src/PCHealth.App/Assets/app.ico preview-256.png preview-32.png

Writes a multi-size .ico (16 to 256 px; the small sizes use a simpler drawing) and two previews. Needs Pillow.
"""
import sys
from PIL import Image, ImageDraw

S = 1024

def tile():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    grad = Image.new("RGBA", (S, S))
    top, bottom = (37, 120, 230), (12, 68, 160)
    gd = ImageDraw.Draw(grad)
    for y in range(S):
        t = y / (S - 1)
        gd.line([(0, y), (S, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(top, bottom)) + (255,))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle((40, 40, S - 40, S - 40), radius=210, fill=255)
    img.paste(grad, (0, 0), mask)
    return img

def draw(detail=True):
    img = tile()
    d = ImageDraw.Draw(img)
    # The drive.
    body = (196, 300, 828, 724)
    d.rounded_rectangle(body, radius=80, fill=(255, 255, 255, 255))
    if detail:
        d.line([(236, 620), (788, 620)], fill=(200, 216, 240, 255), width=14)
        d.ellipse((704, 648, 752, 696), fill=(34, 197, 94, 255))
    # The heartbeat line.
    y = 470 if detail else 512
    pts = [(250, y), (400, y), (455, y - 120), (535, y + 120), (600, y - 60), (640, y), (774, y)]
    d.line(pts, fill=(22, 101, 216, 255), width=58 if detail else 80, joint="curve")
    for x, yy in (pts[0], pts[-1]):
        r = (58 if detail else 80) // 2
        d.ellipse((x - r, yy - r, x + r, yy + r), fill=(22, 101, 216, 255))
    return img

big, small = draw(True), draw(False)
sizes = [256, 128, 64, 48, 40, 32, 24, 20, 16]
frames = [(big if s >= 48 else small).resize((s, s), Image.LANCZOS) for s in sizes]
out = sys.argv[1]
frames[0].save(out, format="ICO", sizes=[(s, s) for s in sizes], append_images=frames[1:])
big.resize((256, 256), Image.LANCZOS).save(sys.argv[2])
small.resize((32, 32), Image.LANCZOS).resize((128, 128), Image.NEAREST).save(sys.argv[3])
