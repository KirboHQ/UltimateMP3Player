# README screenshots, step 4: the pictures of desktop.ps1 (<folder>/shots) and phone.ps1 (<folder>/pshots) made into the README's
# (assets/readme/screens): the desktop window with its corners and shadow, phones in a frame, on the brand's purple.
#   python -I compose.py [<folder>]        (default: ump-readme in TEMP, as the other steps; needs pillow and numpy)
import os, sys, numpy as np
from PIL import Image, ImageDraw, ImageFilter
FOLDER = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.environ["TEMP"], "ump-readme")
D, P = os.path.join(FOLDER, "shots"), os.path.join(FOLDER, "pshots")
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "assets", "readme", "screens")

def rounded(im, r):
    im = im.convert("RGBA")
    m = Image.new("L", im.size, 0)
    ImageDraw.Draw(m).rounded_rectangle((0, 0, im.width - 1, im.height - 1), r, fill=255)
    a = np.minimum(np.array(im.getchannel("A")), np.array(m))
    im.putalpha(Image.fromarray(a))
    return im

def gradient(w, h, stops):
    # diagonal, top-left to bottom-right
    y, x = np.mgrid[0:h, 0:w]
    t = (x / w * 0.6 + y / h * 0.4)
    out = np.zeros((h, w, 3))
    for (p0, c0), (p1, c1) in zip(stops, stops[1:]):
        k = np.clip((t - p0) / (p1 - p0), 0, 1)[..., None]
        sel = ((t >= p0) & (t <= p1))[..., None]
        out = np.where(sel, np.array(c0) * (1 - k) + np.array(c1) * k, out)
    return Image.fromarray(out.astype("uint8"), "RGB").convert("RGBA")

def paste_shadow(canvas, card, xy, blur=28, dy=16, alpha=120):
    sh = Image.new("RGBA", (card.width + blur * 4, card.height + blur * 4), (0, 0, 0, 0))
    a = card.getchannel("A").point(lambda v: v * alpha // 255)
    sh.paste((0, 0, 0, 255), (blur * 2, blur * 2), a)
    sh = sh.filter(ImageFilter.GaussianBlur(blur))
    canvas.alpha_composite(sh, (xy[0] - blur * 2, xy[1] - blur * 2 + dy))
    canvas.alpha_composite(card, xy)

def window(name, scale=1.0):
    im = Image.open(os.path.join(D, name)).convert("RGB")
    if scale != 1: im = im.resize((round(im.width * scale), round(im.height * scale)), Image.LANCZOS)
    return rounded(im, round(9 * scale))

def phone(name, height):
    screen = Image.open(os.path.join(P, name)).convert("RGB")
    # No status bar (the frame's corners would cut it): the background just under it, carried up.
    a = np.array(screen)
    a[:100] = a[100:104].mean(axis=0).astype("uint8")
    screen = Image.fromarray(a)
    w = round(screen.width * height / screen.height)
    screen = screen.resize((w, height), Image.LANCZOS)
    r = round(height * 0.068)
    b = max(8, round(height * 0.014))
    frame = Image.new("RGBA", (w + 2 * b, height + 2 * b), (0, 0, 0, 0))
    d = ImageDraw.Draw(frame)
    d.rounded_rectangle((0, 0, frame.width - 1, frame.height - 1), r + b, fill=(14, 13, 18, 255), outline=(70, 66, 82, 255), width=3)
    frame.alpha_composite(rounded(screen, r), (b, b))
    return frame

PURPLE = [(0.0, (40, 26, 74)), (0.55, (78, 44, 110)), (1.0, (118, 52, 104))]

def save(im, name, q=86):
    im.save(os.path.join(OUT, name), "WEBP", quality=q, method=6)
    print(name, im.size, os.path.getsize(os.path.join(OUT, name)) // 1024, "KB")

# Desktop pictures: the window alone, on transparency, as before.
for src, dst in [("lyrics.png", "player.webp"), ("search.png", "search.webp"), ("playlist.png", "library.webp"), ("dj.png", "dj.webp")]:
    card = window(src)
    m = 44
    canvas = Image.new("RGBA", (card.width + 2 * m, card.height + 2 * m + 12), (0, 0, 0, 0))
    paste_shadow(canvas, card, (m, m - 8), blur=22, dy=12, alpha=110)
    save(canvas, dst)

# Hero: the window and, over its right side, the phone's player.
W, H = 1800, 1100
hero = gradient(W, H, PURPLE)
card = window("home.png", 0.9)
paste_shadow(hero, card, (64, 64), blur=30, dy=18, alpha=140)
ph = phone("player.png", 880)
paste_shadow(hero, ph, (W - ph.width - 66, H - ph.height - 46), blur=30, dy=18, alpha=150)
save(rounded(hero, 36), "hero.webp")

# Phones: home in light, a playlist, synced lyrics, search.
W, H = 2008, 1011
row = gradient(W, H, PURPLE)
phones = [phone(n, 870) for n in ("home_light.png", "playlist.png", "lyrics.png", "search.png")]
gap = (W - sum(p.width for p in phones)) / (len(phones) + 1)
x = gap
for p in phones:
    paste_shadow(row, p, (round(x), (H - p.height) // 2 - 6), blur=26, dy=16, alpha=150)
    x += p.width + gap
save(rounded(row, 36), "phone.webp")
