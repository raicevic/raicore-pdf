# Apache Tika sample files

Real-world OpenDocument text documents from the test data of [Apache Tika](https://tika.apache.org/), used to test the
OpenDocument text converter (`OdtToPdf`): documents written by OpenOffice.org 2 and 3 and LibreOffice (ODF 1.0 to
1.3) with tables, lists, footnotes, headers and footers, images, embedded objects, tracked changes, comments, macros
and CJK text, a flat XML document, an encrypted document and a file that is not a ZIP package.

- Source: <https://github.com/apache/tika/tree/6ef8f501b1610fcb36d275332d2337e3113de74d>, files
  `tika-parsers/tika-parsers-standard/tika-parsers-standard-modules/tika-parser-miscoffice-module/src/test/resources/test-documents/`
  `testFooter.odt`, `testNPEOpenDocument.odt`, `testODFwithOOo3.odt`, `testODTEmbedded.odt`,
  `testODTEmbeddedImageLink.odt`, `testODTEncrypted.odt` (encrypted), `testODTMacro.fodt`, `testODTMacro.odt`,
  `testODTNoMeta.odt`, `testODTStyles2.odt`, `testODTStyles3.odt`, `testODT_svgTitleInStyledSpan.odt`,
  `testODTnotaZipFile.odt` (not a ZIP package), `testOpenOffice2.odt`, `testStyles.odt` and
  `versions/LibreOfficeWriter_odt_1.3.odt`, and
  `tika-parsers/tika-parsers-standard/tika-parsers-standard-integration-tests/src/test/resources/test-documents/testPhoneNumberExtractor.odt`,
  unchanged.
- License: Apache License 2.0, as part of Apache Tika. Copyright 2007-2026 The Apache Software Foundation. This product
  includes software developed at The Apache Software Foundation (<https://www.apache.org/>).

The paragraph texts odfpy, an independent reader, finds in them are in `../Documents/Odt/expected.json`
(`../Documents/Odt/make-expected.py`).
