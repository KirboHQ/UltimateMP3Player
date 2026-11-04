# The icon fonts the apps carry, cut from the whole ones in tools/icon-fonts down to the icons of Icons.Map_
# (src/UltimateMP3Player.Avalonia/Infrastructure.cs): 5 MB less in every Linux, macOS, Android and iOS package.
# Run it after adding a code there (Headless fails if one is missing). Needs fontTools: pip install fonttools
import os
import re

from fontTools import subset
from fontTools.pens.recordingPen import DecomposingRecordingPen
from fontTools.ttLib import TTFont

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
assets = os.path.join(root, "src", "UltimateMP3Player.Avalonia", "Assets", "Fonts")
source = open(os.path.join(root, "src", "UltimateMP3Player.Avalonia", "Infrastructure.cs"), encoding="utf-8").read()
table = source[source.index("Map_ = new()"):source.index("FilledCodes = new()")]
char = r"'(\\u[0-9a-fA-F]{4}|.)'"


def code(text):
    return int(text[2:], 16) if text.startswith("\\u") else ord(text)


def outlines(font):
    glyphs, names = font.getGlyphSet(), font.getBestCmap()
    drawn = {}
    for c, name in names.items():
        pen = DecomposingRecordingPen(glyphs)
        glyphs[name].draw(pen)
        drawn[c] = (pen.value, glyphs[name].width)
    return drawn


codes = {code(fluent) for _, fluent in re.findall(r"\[" + char + r"\]\s*=\s*" + char, table)}
assert codes, "no icons found in Icons.Map_"

found = set()
for name in ("Regular", "Filled"):
    path = os.path.join(root, "tools", "icon-fonts", f"FluentSystemIcons-{name}.ttf")
    options = subset.Options()
    options.layout_features = []
    options.hinting = False
    options.notdef_outline = True
    # The apps ask for the font by its family name.
    options.name_IDs = ["*"]
    options.name_languages = ["*"]
    options.name_legacy = True
    # The same icons make the same file: no change for git to see.
    options.recalc_timestamp = False
    # (load_font keeps the glyphs' boxes as they are: these fonts declare xMin 0 for icons that start further right, and
    # boxes worked out again would move every icon left when drawn, by 10% of its width.)
    cut = subset.load_font(path, options)
    family = cut["name"].getDebugName(1)
    cutter = subset.Subsetter(options)
    cutter.populate(unicodes=codes)
    cutter.subset(cut)
    out = os.path.join(assets, f"FluentSystemIcons-{name}.ttf")
    subset.save_font(cut, out, options)

    made = TTFont(out)
    assert made["name"].getDebugName(1) == family, f"{name}: the family name changed"
    before, after = outlines(TTFont(path)), outlines(made)
    moved = [c for c in after if after[c] != before[c]]
    assert not moved, f"{name}: drawn differently: " + " ".join(f"{c:04X}" for c in moved)
    found |= set(after)
    print(f"{name}: {len(after)} icons, {os.path.getsize(out) / 1024:.0f} KB, family {family}")

missing = codes - found
assert not missing, "not in the whole fonts: " + " ".join(f"{c:04X}" for c in sorted(missing))
