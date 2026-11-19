# Apache POI sample files

Real-world compound files (`.msg`, `.doc`, `.xls`, `.ppt`) and presentations (`.pptx`) from the test data of
[Apache POI](https://poi.apache.org/), used to test the Compound File Binary reader, the Outlook message converter and
the PPTX converter.

- Source: <https://github.com/apache/poi/tree/f66927954a29cf1f5b86614fdf1816e6321c8aea/test-data>, files
  `hsmf/quick.msg`, `hsmf/simple_test_msg.msg`, `hsmf/HTMLBodyBinary_UTF-8.msg`, `hsmf/ASCII_CP1251_LCID1049.msg`,
  `document/documentProperties.doc`, `spreadsheet/chinese-provinces.xls` and `slideshow/Single_Coloured_Page.ppt`,
  and the presentations `slideshow/54542_cropped_bitmap.pptx`, `SmartArt.pptx`, `backgrounds.pptx`, `bar-chart.pptx`,
  `bug58144-headers-footers-2007.pptx`, `crop-to-0.pptx`, `layouts.pptx`, `missing-blip-fill.pptx`,
  `picture-transparency.pptx`, `sample.pptx`, `shapes.pptx`, `table_test.pptx` and `themes.pptx`, unchanged.
- License: Apache License 2.0, as part of Apache POI. Copyright 2003-2026 The Apache Software Foundation. This product
  includes software developed at The Apache Software Foundation (<https://www.apache.org/>).

`expected.json` records what olefile and extract-msg, independent readers, find in them (`make-expected.py`); what
python-pptx finds in the presentations is in `../Slides/expected.json` (`../Slides/make-expected.py`).
