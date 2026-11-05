"""Disegna assets/lex.ico (e assets/lex.png) con i colori e i font del design system Lex Arcana.

Ogni dimensione è disegnata a parte, perché a 16 pixel una scritta non si legge:
  256, 128, 64, 48  "LEX" in Cinzel dorato e "arcana" in EB Garamond corsivo, dentro una cornice pompeiana
  32                solo "LEX"
  16                la "L" come monogramma
Uso: python3 tools/genera-icona.py
"""
import io, os, struct
from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FONTS = os.path.join(ROOT, "src", "LexArcana.Visione", "Assets", "Fonts")
BASALT, POMPEIAN, POMPEIAN_LIGHT, GILT, INK = (0x26, 0x20, 0x1c), (0x9a, 0x2b, 0x1e), (0xe8, 0x80, 0x6a), (0xc4, 0xa0, 0x58), (0xee, 0xe4, 0xd2)

def font(name, size, weight=None, italic=False):
    f = ImageFont.truetype(os.path.join(FONTS, name), size)
    try:
        if weight: f.set_variation_by_axes([weight] if not italic else [weight])
    except Exception:
        pass
    return f

def centered(draw, text, f, cy, fill):
    l, t, r, b = draw.textbbox((0, 0), text, font=f)
    draw.text(((S - (r - l)) / 2 - l, cy - (b - t) / 2 - t), text, font=f, fill=fill)

def render(size):
    global S
    S = 1024                      # si disegna in grande e si riduce: bordi morbidi a ogni dimensione
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    radius = int(S * 0.16)
    d.rounded_rectangle((0, 0, S - 1, S - 1), radius, fill=BASALT)
    # cornice: una linea pompeiana e, più dentro, un filo dorato
    m1, m2 = int(S * 0.055), int(S * 0.1)
    d.rounded_rectangle((m1, m1, S - 1 - m1, S - 1 - m1), radius - m1, outline=POMPEIAN, width=int(S * 0.035))
    if size >= 48:
        d.rounded_rectangle((m2, m2, S - 1 - m2, S - 1 - m2), radius - m2, outline=GILT, width=max(2, int(S * 0.008)))

    if size >= 48:
        centered(d, "LEX", font("Cinzel.ttf", int(S * 0.36), 700), S * 0.44, GILT)
        # un filo pompeiano tra le due parole
        d.line((S * 0.28, S * 0.635, S * 0.72, S * 0.635), fill=POMPEIAN_LIGHT, width=int(S * 0.012))
        centered(d, "arcana", font("EBGaramond.ttf", int(S * 0.2), 500), S * 0.745, INK)
    elif size >= 32:
        centered(d, "LEX", font("Cinzel.ttf", int(S * 0.42), 800), S * 0.5, GILT)
    else:
        centered(d, "L", font("Cinzel.ttf", int(S * 0.78), 800), S * 0.52, GILT)
    return img.resize((size, size), Image.LANCZOS)

def write_ico(path, images):
    """Un .ico con un'immagine PNG per dimensione (lo leggono Windows Vista e successivi)."""
    blobs = []
    for im in images:
        buf = io.BytesIO()
        im.save(buf, format="PNG")
        blobs.append(buf.getvalue())
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b""
    for im, blob in zip(images, blobs):
        w = 0 if im.width >= 256 else im.width
        entries += struct.pack("<BBBBHHII", w, w, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    with open(path, "wb") as f:
        f.write(header + entries + b"".join(blobs))

sizes = [256, 128, 64, 48, 32, 24, 16]
images = [render(s) for s in sizes]
out = os.path.join(ROOT, "assets")
os.makedirs(out, exist_ok=True)
write_ico(os.path.join(out, "lex.ico"), images)
images[0].save(os.path.join(out, "lex.png"))
render(512).save(os.path.join(out, "lex-512.png"))
print("Scritti assets/lex.ico (" + ", ".join(map(str, sizes)) + " px), assets/lex.png, assets/lex-512.png")
