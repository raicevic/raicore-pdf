"""Records in expected.json what independent sources say about the Word 97-2003 (.doc) samples of this folder.

No Python library reads the text of .doc files, so the expected text is quoted from the unit tests that come with the
samples, in Apache POI (commit f66927954a29cf1f5b86614fdf1816e6321c8aea, poi-scratchpad/src/test/java/org/apache/poi/
hwpf/...) and Apache Tika (commit 6ef8f501b1610fcb36d275332d2337e3113de74d, tika-parsers/tika-parsers-standard/
tika-parsers-standard-modules/tika-parser-microsoft-module/src/test/java/org/apache/tika/parser/microsoft/
WordParserTest.java); each entry names its test file and line. olefile (an independent compound file reader) adds the
title and author of each file's summary information and whether it has the streams of a Word 97-2003 document.

Per document: "text" lists paragraphs that follow each other in this order (POI tests that read paragraphs by index);
"anywhere" text that is somewhere in the document (Tika's and POI's assertContains, headers, footers and notes);
"labels" list labels with the start of their paragraph's text (whitespace between them is not compared); "error" the
exception a converter throws ("NotSupported" for encrypted and Word 6/95 files, "Format" for damaged ones). Run with a
venv that has olefile; from this folder: python make-expected.py"""
import glob
import json

import olefile

POI = "poi-scratchpad/src/test/java/org/apache/poi/hwpf/"
TIKA = "WordParserTest.java"

SOURCES = {
    # POI extractor/TestWordExtractor.java lines 63-78 (p_text1, the paragraphs of test2.doc).
    "test2.doc": {
        "source": POI + "extractor/TestWordExtractor.java:63",
        "text": [
            "This is a simple word document",
            "It has a number of paragraphs in it",
            "Some of them even feature bold, italic and underlined text",
            "This bit is in a different font and size",
            "This bit features some red text.",
            "It is otherwise very very boring.",
        ],
    },
    # POI usermodel/TestLists.java lines 42-192 (paragraphs 0 to 38 by index).
    "Lists.doc": {
        "source": POI + "usermodel/TestLists.java:42",
        "text": [
            "Heading Level 1",
            "This document has different lists in it for testing",
            "Unordered list 1", "UL 2", "UL 3",
            "Next up is an ordered list:",
            "Ordered list 1", "OL 2", "OL 3",
            "Now for an un-ordered list with a different bullet style:",
            "Tick 1", "Tick 2",
            "Multi-level un-ordered list:",
            "ML 1:1", "ML 1:2", "ML 2:1", "ML 2:2", "ML 2:3", "ML 3:1", "ML 4:1", "ML 5:1", "ML 5:2", "ML 2:4", "ML 1:3",
            "Multi-level ordered list:",
            "OL 1", "OL 2", "OL 2.1", "OL 2.2", "OL 2.2.1", "OL 2.2.2", "OL 2.2.2.1", "OL 2.2.3", "OL 3",
            "Finally we want some indents, to tell the difference",
            "Indented once", "Indented twice", "Indented three times",
            "The end!",
        ],
    },
    # POI extractor/TestWordExtractor.java lines 190 and 213 (header and footer text).
    "ThreeColHeadFoot.doc": {
        "source": POI + "extractor/TestWordExtractor.java:190",
        "anywhere": ["First header column!\tMid header Right header!", "Footer Left\tFooter Middle Footer Right"],
    },
    # POI extractor/TestWordExtractor.java lines 200 and 223.
    "HeaderFooterUnicode.doc": {
        "source": POI + "extractor/TestWordExtractor.java:200",
        "anywhere": ["This is a simple header, with a \u20ac euro symbol in it.", "The footer, with Moli\u00e8re, has Unicode in it."],
    },
    # POI usermodel/TestHeaderStories.java lines 162-173 (a first page with its own header and footer).
    "DiffFirstPageHeadFoot.doc": {
        "source": POI + "usermodel/TestHeaderStories.java:162",
        "anywhere": [
            "I am the header on the first page, and I\u2019m nice and simple",
            "First header column!\tMid header Right header!",
            "The footer of the first page",
            "Footer Left\tFooter Middle Footer Right",
        ],
    },
    # POI usermodel/TestHeaderStories.java lines 140-149 (headers of even and odd pages, as tables).
    "PageSpecificHeadFoot.doc": {
        "source": POI + "usermodel/TestHeaderStories.java:140",
        "anywhere": ["[This is an Even Page, with a Header]", "August 20, 2008", "[ODD Page Header text]", "Footer Left\tFooter Middle Footer Right"],
    },
    # POI extractor/TestWordExtractor.java lines 234 and 248 (the comment, line 260, is left out by the converter).
    "footnote.doc": {
        "source": POI + "extractor/TestWordExtractor.java:234",
        "anywhere": ["TestFootnote", "TestEndnote"],
        "absent": ["TestComment"],
    },
    # POI extractor/TestWordExtractor.java lines 323-324 (a fast-saved document).
    "rasp.doc": {
        "source": POI + "extractor/TestWordExtractor.java:323",
        "anywhere": ["\u0425\u0425\u0425\u0425\u0425", "\u0423\u0423\u0423\u0423\u0423"],
    },
    # POI extractor/TestWordExtractor.java line 335 (the text starts with this word).
    "Bug48075.doc": {
        "source": POI + "extractor/TestWordExtractor.java:335",
        "text": ["\u041f\u0440\u0438\u043b\u043e\u0436\u0435\u043d\u0438\u0435"],
    },
    # POI extractor/TestWordExtractor.java line 421 (the word is in capitals by its formatting).
    "capitalized.doc": {
        "source": POI + "extractor/TestWordExtractor.java:421",
        "text": ["The following word is: CAPITALIZED."],
    },
    # POI converter/TestWordToHtmlConverter.java line 105 (merged cells spanning 3 and 2 columns).
    "table-merges.doc": {"source": POI + "converter/TestWordToHtmlConverter.java:105", "colspans": [3, 2]},
    # POI usermodel/TestBugs.java line 566 (eight pictures: a PNG and two bitmaps, the others of unknown types).
    "Bug51890.doc": {"source": POI + "usermodel/TestBugs.java:566", "pictures": 3},
    # POI usermodel/TestPictures.java line 346 (one PNG picture).
    "PngPicture.doc": {"source": POI + "usermodel/TestPictures.java:346", "pictures": 1},
    # Tika WordParserTest.java lines 115-134.
    "testWORD.doc": {
        "source": TIKA + ":115",
        "text": ["Heading Level 1", "Heading Level 3", "BOLD", "ITALIC"],
        "anywhere": ["Sample Word Document", "Tika", "This one"],
        "links": ["http://tika.apache.org/"],
    },
    # Tika WordParserTest.java lines 251-316 (testVarious).
    "testWORD_various.doc": {
        "source": TIKA + ":251",
        "anywhere": [
            "Footnote appears here", "This is a footnote.", "This is the header text.", "This is the footer text.",
            "Here is a text box", "Bold", "italic", "underline", "superscript", "subscript", "Here is a citation:",
            "Figure 1 This is a caption for Figure 1", "(Kramer)",
            "Row 1 Col 1 Row 1 Col 2 Row 1 Col 3 Row 2 Col 1 Row 2 Col 2 Row 2 Col 3",
            "Row 1 column 1", "Row 2 column 1", "Row 1 column 2", "Row 2 column 2",
            "This is a hyperlink", "Here is a list:", "Bullet 1", "Bullet 2", "Bullet 3",
            "Here is a numbered list:", "Number bullet 1", "Number bullet 2", "Number bullet 3",
            "Keyword1 Keyword2", "Subject is here", "Suddenly some Japanese text:",
            "\uff08\uff27\uff28\uff31\uff09", "\u30be\u30eb\u30b2\u3068\u5c3e\u5d0e\u3001\u6de1\u3005\u3068\u6700\u671f",
            "And then some Gothic text:", "\U00010332\U0001033f\U00010344\U00010339\U00010343\U0001033a",
        ],
    },
    # Tika WordParserTest.java lines 481-506 (testDOCParagraphNumbering).
    "testWORD_numbered_list.doc": {
        "source": TIKA + ":481",
        "labels": [
            "1) This", "a) Is", "i) A multi", "ii) Level", "1. Within cell 1", "b. Cell b", "iii) List", "2) foo",
            "ii) baz", "ii) foo", "II. bar", "6. six", "7. seven", "a. seven a", "e. seven e", "2. A ii 2",
            "3. page break list 3", "Some-1-CrazyFormat Greek numbering with crazy format - alpha", "1.1.1. 1.1.1",
        ],
        # Tika lines 502-504: "add a list here" and "comment list 1" are in a comment, which the converter leaves out.
        "absent": ["add a list here"],
    },
    # Tika WordParserTest.java lines 513-549 (testDOCOverrideParagraphNumbering).
    "testWORD_override_list_numbering.doc": {
        "source": TIKA + ":513",
        "labels": [
            "1.1.1.1...1 1.1.1.1...1", "1st.2.3someText 1st.2.3someText", "1st.2.2someOtherText.1 1st.2.2someOtherText.1",
            "5th 5th", "1.a.I 1.a.I", "1.b.III 1.b.III", "2.a.I 2.a.I", "2.b 2.b", "(1)) (1))", "2.17 2.17",
            "2.18.2.1 2.18.2.1", "00 00", "01 01", "01. 01.", "01..1 01..1", "02 02",
            # Lines 538-540, without the ">" of the element before: a level whose format an override changes.
            "A A", "B B", "C C",
        ],
    },
    # Tika WordParserTest.java lines 136-145 (three pictures, then this text).
    "testWORD_3imgs.doc": {"source": TIKA + ":136", "anywhere": ["The end!"], "pictures": 3},
    # Tika WordParserTest.java lines 593-596 (a link, part of it bold).
    "testWORD_boldHyperlink.doc": {"source": TIKA + ":593", "anywhere": ["hyper link", "; bold"], "links": ["http://tika.apache.org/"]},
    # Tika WordParserTest.java lines 415-432 (links in the header, and no field instruction shown).
    "testWORD_header_hyperlink.doc": {
        "source": TIKA + ":415",
        "anywhere": ["example.com"],
        "absent": ["HYPERLINK"],
        "links": ["http://tw-systemhaus.de", "mailto:ab@example.com"],
    },
    # Tika WordParserTest.java line 683 (an EQ field with phonetic guides; its result is shown).
    "testWORD_phonetic.doc": {"source": TIKA + ":683", "absent": ["jc2"]},
    # Tika WordParserTest.java lines 667-673 (password-protected).
    "testWORD_protected_passtika.doc": {"source": TIKA + ":667", "error": "NotSupported"},
    # Tika WordParserTest.java lines 200-212 (a Word 6 document, not Word 97-2003).
    "testWORD6.doc": {"source": TIKA + ":200", "error": "NotSupported"},
    # A file of POI's fuzzing corpus without a table stream.
    "clusterfuzz-testcase-minimized-POIHWPFFuzzer-5418937293340672.doc": {"source": "POI test-data", "error": "Format"},
}

expected = {}
for name in sorted(glob.glob("*.doc")):
    entry = dict(SOURCES.get(name, {}))
    ole = olefile.OleFileIO(name)
    entry["word_document"] = ole.exists("WordDocument")
    meta = ole.get_metadata()
    codepage = (meta.codepage or 1252) & 0xFFFF  # olefile reads the code page as a signed 16-bit value.
    encoding = {1200: "utf-16-le", 65001: "utf-8"}.get(codepage, "cp%d" % codepage)
    entry["title"] = meta.title.decode(encoding, "replace") if meta.title else None
    entry["author"] = meta.author.decode(encoding, "replace") if meta.author else None
    ole.close()
    for key in ("text", "anywhere", "labels", "absent", "links"):
        entry.setdefault(key, [])
    entry.setdefault("error", None)
    expected[name] = entry
with open("expected.json", "w") as f:
    json.dump(expected, f, indent=2, sort_keys=True, ensure_ascii=True)
