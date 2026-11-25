"""Generates src/RaiCore.Pdf.Sheets/Xlsx/PivotStyles.xml.gz: the definitions of Excel's built-in pivot table styles.

They come from Apache POI's presetTableStyles.xml (poi-ooxml, org/apache/poi/xssf/usermodel), the predefined table
styles of ECMA-376 Part 1 (Apache License 2.0, as part of Apache POI), at a pinned commit. Only the pivot table styles
are kept (PivotStyleLight1-28, PivotStyleMedium1-28, PivotStyleDark1-28), each as it is there: its own <dxfs> and a
<tableStyles> with its <tableStyle> elements, which name their formats by index (dxfId), the way a workbook's
styles.xml defines custom table styles. Whitespace between elements is dropped, and the file is gzip without a
timestamp, so the output is reproducible:

    <pivotStyles><PivotStyleLight1><dxfs>...</dxfs><tableStyles><tableStyle ...>...</tableStyle></tableStyles></PivotStyleLight1>...</pivotStyles>

Usage: python3 tools/generate_pivot_styles.py [<presetTableStyles.xml>]
       Without a file, it is downloaded from GitHub at COMMIT.
"""
import gzip
import sys
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

COMMIT = "f66927954a29cf1f5b86614fdf1816e6321c8aea"  # The Apache POI commit of the test data in tests/RaiCore.Pdf.Tests/Fixtures.
URL = f"https://raw.githubusercontent.com/apache/poi/{COMMIT}/poi-ooxml/src/main/resources/org/apache/poi/xssf/usermodel/presetTableStyles.xml"
OUTPUT = Path(__file__).resolve().parent.parent / "src/RaiCore.Pdf.Sheets/Xlsx/PivotStyles.xml.gz"


def strip(element):
    if element.text is not None and element.text.strip() == "":
        element.text = None
    element.tail = None
    for child in element:
        strip(child)


def main():
    data = Path(sys.argv[1]).read_bytes() if len(sys.argv) > 1 else urllib.request.urlopen(URL).read()
    root = ET.fromstring(data)
    pivots = ET.Element("pivotStyles")
    for style in root:
        if style.tag.startswith("PivotStyle"):
            pivots.append(style)
    strip(pivots)
    if len(pivots) != 84:
        raise SystemExit(f"Expected 84 pivot styles, found {len(pivots)}")
    OUTPUT.write_bytes(gzip.compress(ET.tostring(pivots, encoding="utf-8"), compresslevel=9, mtime=0))
    print(f"{OUTPUT}: {len(pivots)} styles")


if __name__ == "__main__":
    main()
