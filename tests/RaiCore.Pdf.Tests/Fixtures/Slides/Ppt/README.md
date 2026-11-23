# PowerPoint 97-2003 sample files

Real-world PowerPoint 97-2003 presentations (`.ppt`) from the test data of [Apache POI](https://poi.apache.org/) and
[Apache Tika](https://tika.apache.org/), used to test the PowerPoint 97-2003 converter (`PptToPdf`). All files are
unchanged.

- Source (Apache POI): <https://github.com/apache/poi/tree/f66927954a29cf1f5b86614fdf1816e6321c8aea/test-data/slideshow>,
  files `basic_test_ppt_file.ppt` (plain text on two slides), `bullets.ppt` (bulleted lists, a user-defined bullet),
  `with_textbox.ppt` (text boxes), `text_shapes.ppt` (preset shapes with text),
  `Single_Coloured_Page_With_Fonts_and_Alignments.ppt` (character formatting), `pictures.ppt` (JPEG, PNG, WMF, PICT and
  EMF pictures), `backgrounds.ppt` (picture, pattern, texture and gradient backgrounds and fills),
  `bug58144-headers-footers-2003.ppt` (a footer set for the slides), `WithLinks.ppt` (hyperlinks),
  `bug55902-mixedFontChineseCharacters.ppt` (Japanese and Chinese text), `60003.ppt` (Bulgarian, Cyrillic text; larger
  than the others, the only Cyrillic sample), `missing-moveto.ppt` (free forms, picture backgrounds, WMF pictures),
  `57272_corrupted_usereditatom.ppt` (a damaged edit history: the chain of user edits loops),
  `Password_Protected-hello.ppt` (encrypted) and `pp40only.ppt` (a PowerPoint 4.0 file).
- Source (Apache Tika): <https://github.com/apache/tika/tree/6ef8f501b1610fcb36d275332d2337e3113de74d>, files
  `tika-parsers/tika-parsers-standard/tika-parsers-standard-modules/tika-parser-microsoft-module/src/test/resources/test-documents/`
  `testPPT.ppt` (a PowerPoint X for Mac slide), `testPPT_groups.ppt` (groups, a table, WordArt, SmartArt) and
  `testPPT_various.ppt` (a table, footnotes, a hyperlink, lists, Japanese text, headers and footers).
- License: Apache License 2.0, as part of Apache POI and Apache Tika. Copyright 2003-2026 The Apache Software
  Foundation. This product includes software developed at The Apache Software Foundation (<https://www.apache.org/>).

`expected.json` records what an independent reader finds in them (`make-expected.py`: olefile and a small record
walker, separate from the converter): the slide size, title and author, and per slide whether it is hidden and the text
of its text records; files it cannot read are classified (encrypted, older versions). It also quotes the text Apache
POI's and Apache Tika's own unit tests expect in these files, with links to the test sources.
