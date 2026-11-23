# Apache POI sample files

Real-world compound files (`.msg`, `.doc`, `.xls`, `.ppt`), presentations (`.pptx`), Word documents (`.docx`) and
WMF and EMF pictures from the test data of [Apache POI](https://poi.apache.org/), used to test the Compound File Binary
reader, the Outlook message converter, the PPTX converter, the Word document converter and the metafile renderer.

- Source: <https://github.com/apache/poi/tree/f66927954a29cf1f5b86614fdf1816e6321c8aea/test-data>, files
  `hsmf/quick.msg`, `hsmf/simple_test_msg.msg`, `hsmf/HTMLBodyBinary_UTF-8.msg`, `hsmf/ASCII_CP1251_LCID1049.msg`,
  `document/documentProperties.doc`, `spreadsheet/chinese-provinces.xls` and `slideshow/Single_Coloured_Page.ppt`,
  and the presentations `slideshow/54542_cropped_bitmap.pptx`, `SmartArt.pptx`, `backgrounds.pptx`, `bar-chart.pptx`,
  `bug58144-headers-footers-2007.pptx`, `crop-to-0.pptx`, `layouts.pptx`, `missing-blip-fill.pptx`,
  `picture-transparency.pptx`, `sample.pptx`, `shapes.pptx`, `table_test.pptx` and `themes.pptx`, unchanged.
- Source of the Word documents: the same commit, files `document/ComplexNumberedLists.docx`,
  `document/ThreeColHeadFoot.docx`, `document/DiffFirstPageHeadFoot.docx`, `document/footnotes.docx`,
  `document/table-alignment.docx`, `document/IllustrativeCases.docx`, `document/bug53475-password-is-pass.docx`
  (encrypted), `document/ExternalEntityInText.docx` (a DTD with an external entity) and `document/deep-table-cell.docx`
  (deeply nested tables), unchanged.
- Source of the WMF and EMF pictures (for the metafile renderer, with POI's expectations from `TestHwmfParsing` and
  `TestHemfPicture`): the same commit, files `slideshow/santa.wmf`, `slideshow/file-45.wmf`,
  `slideshow/empty-polygon-close.wmf`, `slideshow/60677.wmf`, `slideshow/61338.wmf`, `slideshow/nested_wmf.emf`,
  `slideshow/wrench.emf`, `slideshow/bug69924.wmf`, `slideshow/clusterfuzz-testcase-minimized-6701721724125184.wmf`,
  `slideshow/clusterfuzz-testcase-minimized-POIFileHandlerFuzzer-6060921738035200.wmf`,
  `slideshow/clusterfuzz-testcase-minimized-POIFileHandlerFuzzer-6466833057382400.emf`, `spreadsheet/61294.emf`,
  `spreadsheet/SimpleEMF_windows.emf`, `spreadsheet/SimpleEMF_mac.emf` and
  `spreadsheet/clusterfuzz-testcase-minimized-POIFileHandlerFuzzer-5318384433823744.emf`; and documents and a
  presentation with metafile pictures: `document/vector_image.doc`, `document/equation.doc` and
  `slideshow/testPPT_oleWorkbook.ppt` (an embedded workbook's EMF preview), unchanged.
- License: Apache License 2.0, as part of Apache POI. Copyright 2003-2026 The Apache Software Foundation. This product
  includes software developed at The Apache Software Foundation (<https://www.apache.org/>).

`expected.json` records what olefile and extract-msg, independent readers, find in them (`make-expected.py`); what
python-pptx finds in the presentations is in `../Slides/expected.json` (`../Slides/make-expected.py`), and the texts
python-docx finds in the Word documents are in `../Documents/expected.json` (`../Documents/make-documents.py`).
