"""Generates the font fixtures in tests/RaiCore.Pdf.Tests/Fixtures (needs fontTools + brotli).

Usage: python tools/generate_test_fonts.py <path to LiberationSans-Regular.ttf>

NotoSansArabic-Subset.ttf (SIL OFL 1.1, licence in NotoSansArabic-OFL.txt) is made separately from
https://github.com/notofonts/arabic with:
  pyftsubset NotoSansArabic-Regular.ttf --unicodes="U+0020-007E,U+0600-06FF,U+FE70-FEFF,U+200C-200F" \
    --layout-features='*' --output-file=tests/RaiCore.Pdf.Tests/Fixtures/NotoSansArabic-Subset.ttf
"""
import sys
from pathlib import Path
from fontTools.ttLib import TTFont
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.t2CharStringPen import T2CharStringPen

out = Path(__file__).resolve().parent.parent / "tests/RaiCore.Pdf.Tests/Fixtures"
out.mkdir(parents=True, exist_ok=True)

# WOFF and WOFF2 versions of Liberation Sans (WOFF2 with transformed glyf/loca and hmtx).
for flavor in ("woff", "woff2"):
    font = TTFont(sys.argv[1])
    font.flavor = flavor
    if flavor == "woff2":
        from fontTools.ttLib.woff2 import WOFF2FlavorData
        font.flavorData = WOFF2FlavorData(transformedTables={"glyf", "loca", "hmtx"})
    font.save(out / f"LiberationSans-Regular.{flavor}")

# A minimal CFF-based OpenType font: glyphs for "A", "B" and space.
fb = FontBuilder(1000, isTTF=False)
glyphs = [".notdef", "space", "A", "B"]
fb.setupGlyphOrder(glyphs)
fb.setupCharacterMap({0x20: "space", 0x41: "A", 0x42: "B"})
def box(width, inset):
    pen = T2CharStringPen(width, None)
    pen.moveTo((inset, 0)); pen.lineTo((width - inset, 0)); pen.lineTo((width - inset, 700)); pen.lineTo((inset, 700)); pen.closePath()
    return pen.getCharString()
empty = T2CharStringPen(250, None)
charstrings = {".notdef": box(500, 50), "space": empty.getCharString(), "A": box(600, 40), "B": box(650, 60)}
fb.setupCFF("RaiCoreTestCFF-Regular", {"FullName": "RaiCore Test CFF"}, charstrings, {})
fb.setupHorizontalMetrics({".notdef": (500, 50), "space": (250, 0), "A": (600, 40), "B": (650, 60)})
fb.setupHorizontalHeader(ascent=800, descent=-200)
fb.setupNameTable({"familyName": "RaiCore Test CFF", "styleName": "Regular", "fullName": "RaiCore Test CFF Regular", "psName": "RaiCoreTestCFF-Regular"})
fb.setupOS2(sTypoAscender=800, sTypoDescender=-200, usWinAscent=800, usWinDescent=200, sCapHeight=700, sxHeight=500)
fb.setupPost()
fb.save(out / "RaiCoreTestCFF-Regular.otf")
# Minimal TrueType fonts: "A", "grave" and a composite "Agrave" (= A + grave), in two weights,
# saved separately and as a TrueType collection.
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib import TTCollection

def tt_font(style, weight, width_a):
    fb = FontBuilder(1000, isTTF=True)
    fb.setupGlyphOrder([".notdef", "space", "A", "grave", "Agrave"])
    fb.setupCharacterMap({0x20: "space", 0x41: "A", 0x60: "grave", 0xC0: "Agrave"})
    def rect(x0, y0, x1, y1):
        pen = TTGlyphPen(None)
        pen.moveTo((x0, y0)); pen.lineTo((x0, y1)); pen.lineTo((x1, y1)); pen.lineTo((x1, y0)); pen.closePath()
        return pen.glyph()
    composite = TTGlyphPen({"A": None, "grave": None})
    composite.addComponent("A", (1, 0, 0, 1, 0, 0))
    composite.addComponent("grave", (1, 0, 0, 1, 150, 750))
    fb.setupGlyf({
        ".notdef": rect(50, 0, 450, 700), "space": TTGlyphPen(None).glyph(),
        "A": rect(40, 0, width_a - 40, 700), "grave": rect(0, 0, 200, 150), "Agrave": composite.glyph(),
    })
    fb.setupHorizontalMetrics({".notdef": (500, 50), "space": (250, 0), "A": (width_a, 40), "grave": (300, 0), "Agrave": (width_a, 40)})
    fb.setupHorizontalHeader(ascent=900, descent=-200)
    fb.setupNameTable({"familyName": "RaiCore Test TT", "styleName": style, "fullName": f"RaiCore Test TT {style}", "psName": f"RaiCoreTestTT-{style}"})
    fb.setupOS2(usWeightClass=weight, sTypoAscender=900, sTypoDescender=-200, usWinAscent=900, usWinDescent=200,
                sCapHeight=700, sxHeight=500, fsType=0)
    fb.setupPost()
    return fb.font

regular = tt_font("Regular", 400, 600)
bold = tt_font("Bold", 700, 680)
regular.save(out / "RaiCoreTestTT-Regular.ttf")
collection = TTCollection()
collection.fonts = [regular, bold]
collection.save(out / "RaiCoreTestTT.ttc")
print("fixtures written to", out)
