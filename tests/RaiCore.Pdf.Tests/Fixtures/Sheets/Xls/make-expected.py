"""Records in expected.json what xlrd (an independent reader of Excel 97-2003 workbooks) finds in the workbooks of
this folder: the BIFF version, the date system, the number formats of the FORMAT records, each sheet's name,
visibility, merged ranges, hidden rows and columns and column widths, and its cells (up to 400 a sheet, blank cells
left out) with their type, value and XF index; for the files xlrd cannot read, its error.

Under "content", for each sheet (worksheets and chart sheets, hidden ones too) with content the converter does not
support, how many of each kind it has, by the names of SheetDiagnosticKind: found by walking the sheet's BIFF8 records
here (independently of the converter's reader) for drawing objects, embedded charts, comments, conditional formatting,
headers and footers, page breaks and pivot tables, and with xlrd for the cells with rich, rotated or shrunk text.

Run with a venv that has xlrd 2.0.1 or later and olefile; from this folder: python make-expected.py"""
import glob
import json
import os
import struct

import olefile
import xlrd

# The kinds of the OBJ records' common data (FtCmo, [MS-XLS] 2.5.143) that are not counted as shapes: groups and the
# shapes of comments (their NOTE records count), charts (their substreams count) and pictures.
GROUP, CHART, PICTURE, NOTE = 0x00, 0x05, 0x08, 0x19


def records(data, position):
    """The records of a BIFF stream from an offset: (type, data); CONTINUE records are not joined."""
    while position + 4 <= len(data):
        kind, length = struct.unpack_from('<HH', data, position)
        yield kind, data[position + 4:position + 4 + length]
        position += 4 + length


def has_subrecord(obj, wanted):
    """Whether an OBJ record has a subrecord of a type (FtPictFmla 0x0009 for an embedded object)."""
    position = 0
    while position + 4 <= len(obj):
        ft, cb = struct.unpack_from('<HH', obj, position)
        if ft == wanted:
            return True
        if ft in (0x0000, 0x0013):
            return False
        position += 4 + cb
    return False


def biff_content(path):
    """For each BIFF8 sheet: its name, whether it is a chart sheet, and the counts of the record kinds it has."""
    ole = olefile.OleFileIO(path)
    data = ole.openstream('Workbook' if ole.exists('Workbook') else 'Book').read()
    sheets = []
    for kind, rec in records(data, 0):
        if kind == 0x0085:  # BOUNDSHEET: offset, visibility, type, name.
            length, flags = rec[6], rec[7]
            name = rec[8:8 + 2 * length].decode('utf-16-le') if flags & 1 else rec[8:8 + length].decode('latin-1')
            sheets.append((name, struct.unpack_from('<I', rec, 0)[0], rec[5]))
        elif kind == 0x000A:
            break
    result = {}
    for name, offset, sheet_type in sheets:
        counts = {}

        def add(key, n=1):
            if n > 0:
                counts[key] = counts.get(key, 0) + n
        if sheet_type == 2:
            add('ChartSheet')
        depth = 0
        for kind, rec in records(data, offset):
            if kind == 0x0809:  # BOF: the sheet's, or an embedded chart's substream.
                depth += 1
                if depth == 2 and struct.unpack_from('<H', rec, 2)[0] == 0x0020:
                    add('Chart')
                continue
            if kind == 0x000A:
                depth -= 1
                if depth == 0:
                    break
                continue
            if depth != 1 or sheet_type == 2:
                continue
            if kind == 0x005D and len(rec) >= 10 and struct.unpack_from('<H', rec, 0)[0] == 0x15:
                obj_type, obj_flags = struct.unpack_from('<H', rec, 4)[0], struct.unpack_from('<H', rec, 8)[0]
                if obj_flags & 0x10 and obj_type not in (GROUP, CHART, NOTE):
                    add('OleObject' if obj_type == PICTURE and has_subrecord(rec, 0x0009) else 'Picture' if obj_type == PICTURE else 'Shape')
            elif kind == 0x001C:
                add('Comment')
            elif kind in (0x01B1, 0x087A):  # CF, CF12
                add('ConditionalFormatting')
            elif kind in (0x0014, 0x0015) and len(rec) >= 2 and struct.unpack_from('<H', rec, 0)[0] > 0:
                add('HeaderFooter')
            elif kind == 0x089C and len(rec) >= 38:  # HEADERFOOTER: the even and first page texts.
                add('HeaderFooter', sum(1 for i in range(4) if struct.unpack_from('<H', rec, 30 + 2 * i)[0] > 0))
            elif kind in (0x001A, 0x001B):
                add('PageBreak', struct.unpack_from('<H', rec, 0)[0])
            elif kind == 0x00B0:
                add('PivotTable')
        if counts:
            result[name] = counts
    return result


def cell_content(book, content):
    """Adds to the content the cells (that show a value) with rich text, rotated text and shrink to fit, as xlrd reads them."""
    for sheet in book.sheets():
        counts = content.setdefault(sheet.name, {})
        for r in range(sheet.nrows):
            for c in range(sheet.ncols):
                cell = sheet.cell(r, c)
                if cell.ctype in (xlrd.XL_CELL_EMPTY, xlrd.XL_CELL_BLANK) or (cell.ctype == xlrd.XL_CELL_TEXT and cell.value == ''):
                    continue
                alignment = book.xf_list[cell.xf_index].alignment
                for key, present in (('RichText', cell.ctype == xlrd.XL_CELL_TEXT and (r, c) in sheet.rich_text_runlist_map),
                                     ('RotatedText', alignment.rotation != 0), ('ShrinkToFit', alignment.shrink_to_fit)):
                    if present:
                        counts[key] = counts.get(key, 0) + 1
        if not counts:
            del content[sheet.name]

# xlrd's cell types.
TYPES = {xlrd.XL_CELL_TEXT: 'text', xlrd.XL_CELL_NUMBER: 'number', xlrd.XL_CELL_DATE: 'number',
         xlrd.XL_CELL_BOOLEAN: 'boolean', xlrd.XL_CELL_ERROR: 'error'}

expected = {}
for name in sorted(glob.glob('*.xls')):
    try:
        book = xlrd.open_workbook(name, formatting_info=True, logfile=open(os.devnull, 'w'))
    except Exception as error:  # Encrypted, damaged.
        expected[name] = {'error': str(error)}
        continue
    file = {
        'biff': book.biff_version,
        'datemode': book.datemode,
        'formats': {str(f.format_key): f.format_str for f in book.format_list},
        'sheets': [],
    }
    for sheet in book.sheets():
        cells = []
        for r in range(sheet.nrows):
            for c in range(sheet.ncols):
                cell = sheet.cell(r, c)
                if cell.ctype in TYPES and len(cells) < 400:
                    value = cell.value
                    if cell.ctype == xlrd.XL_CELL_ERROR:
                        value = xlrd.error_text_from_code.get(value, '#N/A')
                    elif cell.ctype == xlrd.XL_CELL_TEXT:
                        value = value.replace('\0', '')
                    cells.append([r, c, TYPES[cell.ctype], value, cell.xf_index])
        file['sheets'].append({
            'name': sheet.name,
            'hidden': sheet.visibility != 0,
            'rows': sheet.nrows,
            'columns': sheet.ncols,
            'merged': sorted([r1, c1, r2 - 1, c2 - 1] for r1, r2, c1, c2 in sheet.merged_cells),
            'hiddenRows': sorted(r for r, info in sheet.rowinfo_map.items() if info.hidden),
            'hiddenColumns': sorted(c for c, info in sheet.colinfo_map.items() if info.hidden),
            'columnWidths': {str(c): info.width / 256 for c, info in sorted(sheet.colinfo_map.items())},
            'cells': cells,
        })
    content = biff_content(name) if book.biff_version >= 80 else {}
    cell_content(book, content)
    file['content'] = content
    expected[name] = file
with open('expected.json', 'w') as f:
    json.dump(expected, f, indent=1, sort_keys=True, ensure_ascii=True)
