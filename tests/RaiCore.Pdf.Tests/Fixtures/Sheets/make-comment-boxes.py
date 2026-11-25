"""Writes the fixtures of comments printed as displayed on the sheet: Apache POI's SimpleWithComments workbooks (their
third comment, on B3, is shown on the sheet) with only their first sheet's page setup changed to print comments as
displayed. Formatting/CommentBoxes.xlsx gets pageSetup cellComments="asDisplayed"; Xls/CommentBoxes.xls gets the fNotes
bit of its SETUP record (without fEndNotes). Needs olefile (python -m venv venv && venv/bin/pip install olefile); run
from this folder."""
import struct
import zipfile

import olefile

# .xlsx: the same parts in the same order, the first sheet with a page setup after its margins.
with zipfile.ZipFile('Formatting/SimpleWithComments.xlsx') as source, \
        zipfile.ZipFile('Formatting/CommentBoxes.xlsx', 'w', zipfile.ZIP_DEFLATED) as target:
    for info in source.infolist():
        data = source.read(info)
        if info.filename == 'xl/worksheets/sheet1.xml':
            text = data.decode('utf-8')
            margins = text.index('<pageMargins')
            end = text.index('/>', margins) + 2
            text = text[:end] + '<pageSetup cellComments="asDisplayed"/>' + text[end:]
            data = text.encode('utf-8')
        target.writestr(zipfile.ZipInfo(info.filename, date_time=(1980, 1, 1, 0, 0, 0)), data, zipfile.ZIP_DEFLATED)

# .xls: the first SETUP record (of the first sheet) gets fNotes (0x0020) in its options, the stream keeps its size.
with open('Xls/SimpleWithComments.xls', 'rb') as f:
    data = f.read()
with open('Xls/CommentBoxes.xls', 'wb') as f:
    f.write(data)
ole = olefile.OleFileIO('Xls/CommentBoxes.xls', write_mode=True)
stream = bytearray(ole.openstream('Workbook').read())
position = 0
while position + 4 <= len(stream):
    kind, length = struct.unpack_from('<HH', stream, position)
    if kind == 0x00A1:
        options = struct.unpack_from('<H', stream, position + 14)[0]
        struct.pack_into('<H', stream, position + 14, (options | 0x0020) & ~0x0200)
        break
    position += 4 + length
ole.write_stream('Workbook', bytes(stream))
ole.close()
