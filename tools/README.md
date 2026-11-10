# Maintainer tools

These scripts are not part of the build. The Python scripts need [fontTools](https://github.com/fonttools/fonttools):

```sh
python3 -m venv .venv && .venv/bin/pip install fonttools brotli
```

## `generate_standard_font_metrics.py`

Regenerates `src/RaiCore.Pdf/Fonts/StandardFontMetrics.g.cs`, the widths used to measure text in the 14 standard
PDF fonts. They come from the metric-compatible Liberation fonts:

```sh
.venv/bin/python tools/generate_standard_font_metrics.py src/RaiCore.Pdf.Fonts/Liberation
```

## `generate_test_fonts.py`

Regenerates the font fixtures in `tests/RaiCore.Pdf.Tests/Fixtures`: WOFF/WOFF2 versions of Liberation Sans, a tiny
CFF-based OpenType font, and tiny TrueType fonts with a composite glyph (also as a `.ttc` collection).

```sh
.venv/bin/python tools/generate_test_fonts.py src/RaiCore.Pdf.Fonts/Liberation/LiberationSans-Regular.ttf
```

## `generate_line_break_data.py`

Regenerates `src/RaiCore.Pdf.Layout/Text/LineBreakData.g.cs` (Line_Break classes plus the East Asian width,
Extended_Pictographic and general category bits the UAX #14 rules need) from the Unicode Character Database.
When updating the Unicode version, also replace `tests/RaiCore.Pdf.Tests/Fixtures/Unicode/LineBreakTest.txt.gz`
with the matching `LineBreakTest.txt` (gzipped); the conformance test checks that the versions agree.

```sh
base=https://www.unicode.org/Public/UCD/latest/ucd
mkdir -p /tmp/ucd && for f in LineBreak.txt EastAsianWidth.txt emoji/emoji-data.txt extracted/DerivedGeneralCategory.txt auxiliary/LineBreakTest.txt; do
  curl -sSfL -o /tmp/ucd/$(basename $f) $base/$f; done
python3 tools/generate_line_break_data.py /tmp/ucd
gzip -9 -c /tmp/ucd/LineBreakTest.txt > tests/RaiCore.Pdf.Tests/Fixtures/Unicode/LineBreakTest.txt.gz
```

## `generate_test_images.py`

Regenerates the JPEG and GIF fixtures in `tests/RaiCore.Pdf.Tests/Fixtures/Images` (needs Pillow): images with four
colored quadrants in RGB, progressive, gray and Adobe CMYK JPEG, all eight EXIF orientations (each stored so that it
displays upright), and plain, interlaced, transparent and animated GIFs. PNG coverage comes from PngSuite, which is
committed as-is in `Fixtures/PngSuite`.

## `generate_webp_bmp_fixtures.py`

Regenerates the BMP and WebP fixtures in `tests/RaiCore.Pdf.Tests/Fixtures/Bmp` and `Fixtures/WebP` (needs Pillow),
each with a PNG of the pixels it must decode to. The BMPs are written byte by byte to cover every header version, bit
depth, compression and mask layout (cross-checked with Pillow where it reads the variant). The WebPs are encoded with
libwebp, through Pillow and, for settings Pillow does not expose (simple loop filter, token partitions, alpha
filters), through `ctypes` on the libwebp library Pillow bundles; their PNGs are libwebp's own decoding.

```sh
.venv/bin/pip install pillow && .venv/bin/python tools/generate_webp_bmp_fixtures.py
```

## `font-verification/`

Cross-checks the font parser against fontTools on a large set of real fonts, which unit tests cannot cover:

- every cmap entry (code point to glyph ID) and advance width,
- the TrueType subsetter: fontTools must load each subset and find identical outlines and widths,
- decoded WOFF/WOFF2 output (written as `decoded-woff*.ttf`; compare with the original in fontTools).

```sh
find /System/Library/Fonts /usr/share/fonts -type f \( -iname '*.ttf' -o -iname '*.otf' -o -iname '*.ttc' \) > /tmp/fonts.txt
dotnet build tools/font-verification
tr '\n' '\0' < /tmp/fonts.txt | xargs -0 dotnet tools/font-verification/bin/Debug/net10.0/FontVerification.dll /tmp/font-check
.venv/bin/python tools/font-verification/compare.py /tmp/font-check
```

Last run (macOS 27, 2026-11-02): 800 faces matched fontTools exactly and 662 TrueType subsets validated. The
only differences are symbol fonts (Wingdings, Webdings), where RaiCore.Pdf also maps the Windows symbol cmap
(U+F020–U+F0FF), as Windows does. One font with no outlines at all (NISC18030.ttf) is rejected, as intended.
