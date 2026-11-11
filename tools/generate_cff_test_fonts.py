"""Generates the CFF font fixtures in tests/RaiCore.Pdf.Tests/Fixtures for the CFF subsetter (needs fontTools).

Usage: python tools/generate_cff_test_fonts.py <SourceSans3-Regular.otf> <SourceSans3VF-Upright.otf> <NotoSansJP-Regular.otf>

Sources (all SIL OFL 1.1):
  - Source Sans 3 (3.052R), https://github.com/adobe-fonts/source-sans/releases: OTF-source-sans-3.052R.zip
    (SourceSans3-Regular.otf, name-keyed CFF with subroutines) and VF-source-sans-3.052R.zip
    (SourceSans3VF-Upright.otf, CFF2). 'Source' is a Reserved Font Name, so the subsets are renamed
    "RaiCore Sans3" (licence in RaiCoreSans3-OFL.txt).
  - Noto Sans CJK JP, https://github.com/notofonts/noto-cjk: Sans/SubsetOTF/JP/NotoSansJP-Regular.otf
    (CID-keyed CFF with an FDArray; licence in NotoSansCJK-OFL.txt).

The subsets keep their subroutines (pyftsubset only prunes unused ones), so they exercise subroutine subsetting.

RaiCoreTestCFF-Seac.otf is made from scratch: an "Aacute" built with endchar's seac arguments, which the subsetter
does not support (the font is then embedded whole). RaiCoreTestCFF-Subrs.otf has 1300 global and 1300 local
subroutines (bias 1131), of which "A" calls two global ones (nested) and "B" one local one, so a subset with
subroutines kept must renumber them with bias 107; its unitsPerEm is 2048 (a FontMatrix that is not the default).
RaiCoreTestCFF2-Long.otf is a CFF2 font (also 2048 units per em) with blends (in charstrings and in the Private DICT)
and with operators taking more than CFF's limit of 48 arguments, which the conversion to CFF must split.
"""
import sys
from pathlib import Path
from fontTools.ttLib import TTFont
from fontTools import subset
from fontTools.fontBuilder import FontBuilder
from fontTools.misc.psCharStrings import T2CharString
from fontTools.cffLib import GlobalSubrsIndex, SubrsIndex

out = Path(__file__).resolve().parent.parent / "tests/RaiCore.Pdf.Tests/Fixtures"


def make_subset(source, target, unicodes="", text="", rename=None):
    options = subset.Options()
    options.layout_features = []  # No GSUB/GPOS: the tests do not shape, and the fixtures stay small.
    options.notdef_outline = True
    font = TTFont(source)
    subsetter = subset.Subsetter(options)
    subsetter.populate(unicodes=subset.parse_unicodes(unicodes) if unicodes else [], text=text)
    subsetter.subset(font)
    if rename:
        for record in font["name"].names:
            value = record.toUnicode()
            for old, new in rename:
                value = value.replace(old, new)
            record.string = value
        if "CFF " in font:
            cff = font["CFF "].cff
            cff.fontNames = [name.replace("SourceSans3", "RaiCoreSans3") for name in cff.fontNames]
            top = cff.topDictIndex[0]
            for key in ("FullName", "FamilyName", "Notice", "Copyright"):
                if hasattr(top, key):
                    value = getattr(top, key)
                    for old, new in rename:
                        value = value.replace(old, new)
                    setattr(top, key, value)
    font.save(out / target)


rename = [("Source Sans 3", "RaiCore Sans3"), ("SourceSans3", "RaiCoreSans3"), ("Source Sans", "RaiCore Sans")]
make_subset(sys.argv[1], "RaiCoreSans3-Subset.otf", unicodes="U+0020-007E,U+00A0-017F,U+2013-2014,U+2018-201D,U+2026",
            rename=rename)
make_subset(sys.argv[2], "RaiCoreSans3VF-Subset.otf", unicodes="U+0020-007E,U+00A0-00FF", rename=rename)
make_subset(sys.argv[3], "NotoSansJP-Subset.otf", unicodes="U+0020-007E,U+3041-3096,U+30A1-30FA",
            text="日本語のテキストを埋め込みます。"
                 "漢字とひらがなとカタカナ、"
                 "東京都千代田区一二三四五六七八九"
                 "十百千万円年月日時分秒")

fb = FontBuilder(1000, isTTF=False)
fb.setupGlyphOrder([".notdef", "space", "A", "acute", "Aacute"])
fb.setupCharacterMap({0x20: "space", 0x41: "A", 0xB4: "acute", 0xC1: "Aacute"})
box = lambda w, x0, y0, x1, y1: T2CharString(program=[w, x0, y0, "rmoveto", x1 - x0, 0, 0, y1 - y0, x0 - x1, 0, "rlineto", "endchar"])
fb.setupCFF("RaiCoreTestCFF-Seac", {"FullName": "RaiCore Test CFF Seac"}, {
    ".notdef": box(500, 50, 0, 450, 700), "space": T2CharString(program=[250, "endchar"]),
    "A": box(600, 40, 0, 560, 700), "acute": box(300, 50, 750, 250, 900),
    "Aacute": T2CharString(program=[600, 150, 0, 65, 194, "endchar"]),  # seac: A (65) + acute (194), shifted 150
}, {"nominalWidthX": 0, "defaultWidthX": 0})
fb.setupHorizontalMetrics({".notdef": (500, 50), "space": (250, 0), "A": (600, 40), "acute": (300, 50), "Aacute": (600, 40)})
fb.setupHorizontalHeader(ascent=900, descent=-200)
fb.setupNameTable({"familyName": "RaiCore Test CFF Seac", "styleName": "Regular", "psName": "RaiCoreTestCFF-Seac"})
fb.setupOS2(sTypoAscender=900, sTypoDescender=-200, usWinAscent=900, usWinDescent=200, sCapHeight=700, sxHeight=500, fsType=0)
fb.setupPost()
fb.save(out / "RaiCoreTestCFF-Seac.otf")

fb = FontBuilder(2048, isTTF=False)
fb.setupGlyphOrder([".notdef", "space", "A", "B"])
fb.setupCharacterMap({0x20: "space", 0x41: "A", 0x42: "B"})
fb.setupCFF("RaiCoreTestCFF-Subrs", {"FullName": "RaiCore Test CFF Subrs"}, {
    ".notdef": box(500, 50, 0, 450, 700), "space": T2CharString(program=[250, "endchar"]),
    "A": T2CharString(program=[600, 1250 - 1131, "callgsubr", "endchar"]),
    "B": T2CharString(program=[650, 1270 - 1131, "callsubr", "endchar"]),
}, {"nominalWidthX": 0, "defaultWidthX": 0})
cff = fb.font["CFF "].cff
global_subrs = GlobalSubrsIndex()
local_subrs = SubrsIndex()
for i in range(1300):
    global_subrs.append(T2CharString(program=["return"]))
    local_subrs.append(T2CharString(program=["return"]))
global_subrs[1250] = T2CharString(program=[40, 0, "rmoveto", 1260 - 1131, "callgsubr", "return"])
global_subrs[1260] = T2CharString(program=[520, 0, 0, 700, -520, 0, "rlineto", "return"])
local_subrs[1270] = T2CharString(program=[60, 0, "rmoveto", 530, 0, 0, 700, -530, 0, "rlineto", "return"])
cff.GlobalSubrs = global_subrs
cff.topDictIndex[0].Private.Subrs = local_subrs
for name in ("A", "B"):
    cff.topDictIndex[0].CharStrings[name].globalSubrs = global_subrs
fb.setupHorizontalMetrics({".notdef": (500, 50), "space": (250, 0), "A": (600, 40), "B": (650, 60)})
fb.setupHorizontalHeader(ascent=900, descent=-200)
fb.setupNameTable({"familyName": "RaiCore Test CFF Subrs", "styleName": "Regular", "psName": "RaiCoreTestCFF-Subrs"})
fb.setupOS2(sTypoAscender=900, sTypoDescender=-200, usWinAscent=900, usWinDescent=200, sCapHeight=700, sxHeight=500, fsType=0)
fb.setupPost()
fb.save(out / "RaiCoreTestCFF-Subrs.otf")

fb = FontBuilder(2048, isTTF=False)
names = [".notdef", "space", "A", "B", "C", "D", "E", "F", "G"]
fb.setupGlyphOrder(names)
fb.setupCharacterMap({0x20: "space", **{0x41 + i: name for i, name in enumerate(names[2:])}})
fb.setupNameTable({"familyName": "RaiCore Test CFF2", "styleName": "Regular", "psName": "RaiCoreTestCFF2-Long"})
fb.setupFvar([("wght", 200, 400, 900, "Weight")], [])
start = [100, 0, "rmoveto"]
curve = [20, 30, 20, 0, 20, -30]
fb.setupCFF2({
    ".notdef": T2CharString(program=start + [800, 0, 0, 1400, -800, 0, "rlineto"]),
    "space": T2CharString(program=[]),
    # rlineto with 60 arguments (a zigzag 400 units high), after a blended rmoveto.
    "A": T2CharString(program=[100, 20, 1, "blend", 0, "rmoveto"] + [30, 400, 30, -400] * 15 + ["rlineto"]),
    # rrcurveto with 60 arguments, the first six blended.
    "B": T2CharString(program=start + curve + [5, 5, 5, 5, 5, 5, 6, "blend"] + curve * 9 + ["rrcurveto"]),
    # hvcurveto with 13 curves and the final extra argument (53).
    "C": T2CharString(program=start + [40, 30, 30, 40] * 13 + [10, "hvcurveto"]),
    # hhcurveto with dy1 and 12 curves (49), then vhcurveto with 14 curves (56).
    "D": T2CharString(program=start + [10] + [40, 30, 0, 40] * 12 + ["hhcurveto"] + [40, 30, 30, 40] * 14 + ["vhcurveto"]),
    # rcurveline with 8 curves and a line (50).
    "E": T2CharString(program=start + curve * 8 + [50, 50, "rcurveline"]),
    # rlinecurve with 22 lines and a curve (50).
    "F": T2CharString(program=start + [20, 10] * 22 + curve + ["rlinecurve"]),
    # vvcurveto with dx1 and 12 curves (49), then hlineto with 50 arguments.
    "G": T2CharString(program=start + [10] + [40, 30, 0, 40] * 12 + ["vvcurveto"] + [30, 20] * 25 + ["hlineto"]),
}, fdArrayList=[{"BlueValues": [[-12, -4], [0, 0], [1400, 30], [1412, 30]], "StdVW": [80, 60], "BlueScale": 0.0625}],
   regions=[{"wght": (0, 1.0, 1.0)}])
fb.setupHorizontalMetrics({name: (1200, 100) for name in names})
fb.setupHorizontalHeader(ascent=1800, descent=-400)
fb.setupOS2(sTypoAscender=1800, sTypoDescender=-400, usWinAscent=1800, usWinDescent=400, sCapHeight=1400, sxHeight=1000, fsType=0)
fb.setupPost()
fb.save(out / "RaiCoreTestCFF2-Long.otf")
print("fixtures written to", out)
