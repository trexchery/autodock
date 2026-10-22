"""Draws the Patch Goblins goblin head (a 16x16 pixel sprite, left half mirrored) and writes every asset built from it:
dock/PatchGoblins/dock.ico (7 sizes, 16 to 256), assets/goblin.png and assets/social-preview.png.
Run: uv run --with pillow dock/tools/make-icon.py"""
import struct, io, sys
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]

PALETTE = {
    ".": None,
    "O": (14, 38, 20),      # outline
    "d": (46, 107, 48),     # shadow green
    "g": (86, 168, 62),     # skin
    "l": (141, 210, 88),    # light skin
    "e": (255, 226, 77),    # eye
    "p": (14, 20, 14),      # pupil
    "t": (245, 240, 220),   # tooth
    "m": (92, 24, 28),      # mouth
    "n": (46, 107, 48),     # nostril
}

# The left half; the right is its mirror, so the goblin is symmetric at every size.
HALF = [
    "........",
    "O.......",
    "OgO.OOOO",
    "OggOOlll",
    "OgggOggl",
    ".Ogggggg",
    "..Oddggg",
    "..Ogeepg",
    "..Ogeeeg",
    "...Ogggd",
    "...Oggdd",
    "...Ogmmm",
    "....Otmt",
    ".....Ogm",
    "......OO",
    "........",
]
SPRITE = [h + h[::-1] for h in HALF]

def sprite(rows=SPRITE):
    for r in rows:
        assert len(r) == 16, (len(r), r)
    img = Image.new("RGBA", (16, 16), (0, 0, 0, 0))
    for y, row in enumerate(rows):
        for x, c in enumerate(row):
            if PALETTE[c]:
                img.putpixel((x, y), PALETTE[c] + (255,))
    return img

def scaled(size):
    """Whole-pixel scaling where the size is a multiple of 16; 24 is drawn from the 32 so no pixel is uneven."""
    if size % 16 == 0:
        return sprite().resize((size, size), Image.NEAREST)
    return scaled(size * 2 if size < 32 else 32 * ((size // 32) + 1)).resize((size, size), Image.BOX)


def write_ico(path, sizes=(16, 24, 32, 48, 64, 128, 256)):
    pngs = []
    for n in sizes:
        buf = io.BytesIO()
        scaled(n).save(buf, "PNG")
        pngs.append(buf.getvalue())
    out = bytearray(struct.pack("<HHH", 0, 1, len(sizes)))
    offset = 6 + 16 * len(sizes)
    for n, data in zip(sizes, pngs):
        out += struct.pack("<BBBBHHII", n % 256, n % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for data in pngs:
        out += data
    Path(path).write_bytes(bytes(out))


def font(names, size):
    for n in names:
        try:
            return ImageFont.truetype(n, size)
        except OSError:
            pass
    return ImageFont.load_default()


def social(path):
    w, h = 1280, 640
    img = Image.new("RGB", (w, h), (13, 24, 16))
    d = ImageDraw.Draw(img)
    for y in range(h):  # a quiet vertical glow, low and dark
        t = y / h
        d.line([(0, y), (w, y)], fill=(int(13 + 10 * t), int(24 + 22 * t), int(16 + 10 * t)))
    # the goblin, whole pixels, with a hard shadow
    g = scaled(384)
    shadow = Image.new("RGBA", g.size, (0, 0, 0, 0))
    shadow.putalpha(g.getchannel("A").point(lambda a: 90 if a else 0))
    img.paste((0, 0, 0), (80 + 12, 128 + 12), shadow.getchannel("A"))
    img.paste(g, (80, 128), g)
    title = font(["C:/Windows/Fonts/consolab.ttf", "C:/Windows/Fonts/segoeuib.ttf", "DejaVuSansMono-Bold.ttf"], 96)
    sub = font(["C:/Windows/Fonts/segoeui.ttf", "DejaVuSans.ttf"], 38)
    small = font(["C:/Windows/Fonts/consola.ttf", "DejaVuSansMono.ttf"], 26)
    d.text((520, 196), "Patch Goblins", font=title, fill=(141, 210, 88))
    d.text((524, 322), "Your Claude accounts. One coding team.", font=sub, fill=(232, 240, 226))
    d.text((524, 396), "A Director plans. Agents build, test,", font=small, fill=(150, 170, 150))
    d.text((524, 432), "review, and open pull requests.", font=small, fill=(150, 170, 150))
    img.save(path, optimize=True)


if __name__ == "__main__":
    write_ico(ROOT / "dock" / "PatchGoblins" / "dock.ico")
    scaled(256).save(ROOT / "assets" / "goblin.png", optimize=True)
    social(ROOT / "assets" / "social-preview.png")
    print("wrote dock.ico, assets/goblin.png, assets/social-preview.png")
