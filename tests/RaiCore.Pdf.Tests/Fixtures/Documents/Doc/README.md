# Word 97-2003 samples

Word 97-2003 documents (`.doc`) from the test data of [Apache POI](https://poi.apache.org/) and
[Apache Tika](https://tika.apache.org/), used to test the Word 97-2003 converter (`DocToPdf`). All files are unchanged.

- Source: <https://github.com/apache/poi/tree/f66927954a29cf1f5b86614fdf1816e6321c8aea/test-data/document>, files
  `test2.doc` (plain paragraphs with formatting), `Lists.doc` (bulleted, numbered and multi-level lists),
  `ThreeColHeadFoot.doc`, `DiffFirstPageHeadFoot.doc`, `PageSpecificHeadFoot.doc` and `HeaderFooterUnicode.doc`
  (headers and footers: first page, even and odd pages, tables in them, Unicode text), `footnote.doc` (a footnote, an
  endnote and a comment), `rasp.doc` (fast-saved, Cyrillic), `Bug48075.doc` (Cyrillic, a page narrower than its
  margins), `capitalized.doc` (capitals by formatting), `table-merges.doc` (merged cells), `PngPicture.doc` (a PNG
  picture) and `clusterfuzz-testcase-minimized-POIHWPFFuzzer-5418937293340672.doc` (damaged: no table stream).
- Source: <https://github.com/apache/tika/tree/6ef8f501b1610fcb36d275332d2337e3113de74d/tika-parsers/tika-parsers-standard/tika-parsers-standard-modules/tika-parser-microsoft-module/src/test/resources/test-documents>,
  files `testWORD.doc` (headings, a nested table, links, summary information), `testWORD_various.doc` (formatting,
  footnote, header and footer, text box, tables, lists, sections with two columns, Japanese and Gothic text),
  `testWORD_numbered_list.doc` and `testWORD_override_list_numbering.doc` (list numbering and its overrides),
  `testWORD_3imgs.doc` (inline and floating PNG and JPEG pictures), `testWORD_boldHyperlink.doc`,
  `testWORD_header_hyperlink.doc` (links, in a header too), `testWORD_phonetic.doc` (an `EQ` field with Japanese
  phonetic guides), `testWORD_protected_passtika.doc` (password-protected) and `testWORD6.doc` (Word 6, an older
  format).
- License: Apache License 2.0, as part of Apache POI and Apache Tika. Copyright The Apache Software Foundation. This
  product includes software developed at The Apache Software Foundation (<https://www.apache.org/>).

No Python library reads the text of `.doc` files, so `expected.json` holds what the samples' own unit tests in POI
and Tika expect (text, list labels, links, errors; each entry names its test file and line) and the title and author
that olefile, an independent compound file reader, finds in the summary information (`make-expected.py`). The tests
also cut and damage these files to check that the converter throws only the documented exceptions.
