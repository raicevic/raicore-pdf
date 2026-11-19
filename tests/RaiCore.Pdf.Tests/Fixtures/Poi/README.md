# Apache POI sample files

Real-world compound files (`.msg`, `.doc`, `.xls`, `.ppt`) from the test data of
[Apache POI](https://poi.apache.org/), used to test the Compound File Binary reader and the Outlook message converter.

- Source: <https://github.com/apache/poi/tree/f66927954a29cf1f5b86614fdf1816e6321c8aea/test-data>, files
  `hsmf/quick.msg`, `hsmf/simple_test_msg.msg`, `hsmf/HTMLBodyBinary_UTF-8.msg`, `hsmf/ASCII_CP1251_LCID1049.msg`,
  `document/documentProperties.doc`, `spreadsheet/chinese-provinces.xls` and `slideshow/Single_Coloured_Page.ppt`,
  unchanged.
- License: Apache License 2.0, as part of Apache POI. Copyright 2003-2026 The Apache Software Foundation. This product
  includes software developed at The Apache Software Foundation (<https://www.apache.org/>).

`expected.json` records what olefile and extract-msg, independent readers, find in them (`make-expected.py`).
