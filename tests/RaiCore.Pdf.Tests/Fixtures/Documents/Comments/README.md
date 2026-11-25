# Comment fixtures

Documents with comments, for the comment options of the word-processing converters
(`DocumentConverterOptions.Comments`), made for this project (Apache License 2.0).

- `comments.docx`, `comments.odt` and `comments.rtf`: written by `make-comments.py` (python-docx for the Word document,
  hand-written ODF XML and RTF), with comments by several authors with initials and dates, a comment of two
  paragraphs and a reply to it, a comment whose text spans two paragraphs, overlapping comments, a comment without a
  range, one in a table and one about a long text. The script also writes small uncompressed copies as fuzzing seeds.
- `expected.json`: the comments independent readers find in these and in the Apache POI and Tika samples with
  comments (`../../Tika/testComment.*`, `../../Tika/testRTF_annotation_spacing.rtf`, `../../Tika/testODFwithOOo3.odt`,
  `../../Poi/comment.docx`, `../Doc/footnote.doc`, `../Doc/testWORD_numbered_list.doc`), written by `make-expected.py`:
  python-docx, odfpy, striprtf, and `doc_comments.py` (olefile and a walk of the Word 97-2003 structures written from
  [MS-DOC]).
