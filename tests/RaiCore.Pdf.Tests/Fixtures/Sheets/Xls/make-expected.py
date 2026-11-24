"""Records in expected.json what xlrd (an independent reader of Excel 97-2003 workbooks) finds in the workbooks of
this folder: the BIFF version, the date system, the number formats of the FORMAT records, each sheet's name,
visibility, merged ranges, hidden rows and columns and column widths, and its cells (up to 400 a sheet, blank cells
left out) with their type, value and XF index; for the files xlrd cannot read, its error.

Under "content", for each sheet (worksheets and chart sheets, hidden ones too) with content the converter does not
support, how many of each kind it has, by the names of SheetDiagnosticKind: found by walking the sheet's BIFF8 records
here (independently of the converter's reader) for drawing objects, embedded charts, comments, conditional formatting,
and pivot tables, and with xlrd for the cells with rich or rotated text (not stacked text, which is shown, nor shrunk
text; headers, footers and page breaks set by hand are printed).
Drawing objects the converter draws are not counted: each OBJ record is matched to the OfficeArt shape whose client
data comes before it in the sheet's MSODRAWING data, and pictures whose BLIP (in the drawing group's BLIP store) is
PNG, JPEG, DIB, TIFF, WMF or EMF, and lines, rectangles, ovals and text boxes of the common shape types without
vertices of their own, are drawn.

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


# The OBJ types of shapes that are drawn (line, rectangle, oval, text box, Office drawing), the OfficeArt shape types
# (MSOSPT) they are drawn for, and the BLIP record types of the pictures that are drawn.
DRAWN_OBJECTS = {0x01, 0x02, 0x03, 0x06, 0x1E}
DRAWN_SHAPES = {1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 15, 16, 20, 32, 55, 56, 66, 67, 68, 69, 70, 109, 110, 116, 120, 202}
DRAWN_BLIPS = {0xF01A, 0xF01B, 0xF01D, 0xF01E, 0xF01F, 0xF029, 0xF02A}


def officeart(data, start, end):
    """The OfficeArt records in [start, end): (type, instance, version, body start, body end)."""
    position = start
    while position + 8 <= end:
        vi, kind, length = struct.unpack_from('<HHI', data, position)
        body_end = min(position + 8 + length, end)
        yield kind, vi >> 4, vi & 0xF, position + 8, body_end
        position = body_end


def blip_types(group):
    """The BLIP record type of each entry of the drawing group's BLIP store (the BLIP in each FBSE record), in order."""
    result = []
    for kind, _, _, start, end in officeart(group, 0, len(group)):
        if kind == 0xF000:
            for inner, _, _, s, e in officeart(group, start, end):
                if inner == 0xF001:
                    for _, _, _, fs, fe in officeart(group, s, e):
                        name = group[fs + 33] if fs + 33 < fe else 0
                        blip = fs + 36 + name
                        result.append(struct.unpack_from('<H', group, blip + 2)[0] if blip + 8 <= fe else None)
    return result


def shapes(data, start, end, found):
    """The shapes (SpContainer records, not the patriarch) in [start, end), in order: (client data end, FSP type,
    properties by id), groups' children included."""
    for kind, _, version, s, e in officeart(data, start, end):
        if kind == 0xF004:
            shape = {'end': None, 'type': None, 'props': {}, 'patriarch': False}
            for inner, instance, _, bs, be in officeart(data, s, e):
                if inner == 0xF00A and be - bs >= 8:
                    shape['type'] = instance
                    shape['patriarch'] = bool(struct.unpack_from('<I', data, bs + 4)[0] & 0x4)
                elif inner == 0xF00B:
                    for i in range(min(instance, (be - bs) // 6)):
                        pid, value = struct.unpack_from('<Hi', data, bs + 6 * i)
                        shape['props'].setdefault(pid & 0x3FFF, value)
                elif inner == 0xF011:
                    shape['end'] = be
            if not shape['patriarch']:
                found.append(shape)
        elif version == 0xF:
            shapes(data, s, e, found)
    return found


def biff_print(path):
    """For each BIFF8 worksheet with any: its header and footer texts (HEADER, FOOTER) and the rows and columns that
    page breaks set by hand start a page at (HORIZONTALPAGEBREAKS, VERTICALPAGEBREAKS), read here."""
    ole = olefile.OleFileIO(path)
    data = ole.openstream('Workbook' if ole.exists('Workbook') else 'Book').read()
    sheets = []
    for kind, rec in records(data, 0):
        if kind == 0x0085:
            length, flags = rec[6], rec[7]
            name = rec[8:8 + 2 * length].decode('utf-16-le') if flags & 1 else rec[8:8 + length].decode('latin-1')
            sheets.append((name, struct.unpack_from('<I', rec, 0)[0], rec[5]))
        elif kind == 0x000A:
            break

    def string(rec):
        count, flags = struct.unpack_from('<HB', rec, 0)
        return rec[3:3 + 2 * count].decode('utf-16-le') if flags & 1 else rec[3:3 + count].decode('latin-1')

    result = {}
    for name, offset, sheet_type in sheets:
        if sheet_type != 0:
            continue
        found = {}
        depth = 0
        for kind, rec in records(data, offset):
            if kind == 0x0809:
                depth += 1
                continue
            if kind == 0x000A:
                depth -= 1
                if depth == 0:
                    break
                continue
            if depth != 1:
                continue
            if kind in (0x0014, 0x0015) and len(rec) >= 3 and string(rec).strip():
                found['header' if kind == 0x0014 else 'footer'] = string(rec)
            elif kind in (0x001B, 0x001A) and len(rec) >= 2:
                count = struct.unpack_from('<H', rec, 0)[0]
                found['rowBreaks' if kind == 0x001B else 'columnBreaks'] = [
                    struct.unpack_from('<H', rec, 2 + 6 * i)[0] for i in range(count) if 2 + 6 * (i + 1) <= len(rec)]
        if found:
            result[name] = found
    return result


def biff_content(path):
    """For each BIFF8 sheet: its name, whether it is a chart sheet, and the counts of the record kinds it has."""
    ole = olefile.OleFileIO(path)
    data = ole.openstream('Workbook' if ole.exists('Workbook') else 'Book').read()
    sheets = []
    group = b''
    last = None
    for kind, rec in records(data, 0):
        if kind == 0x00EB or (kind == 0x003C and last == 0x00EB):  # MSODRAWINGGROUP and its CONTINUE records.
            group += rec
            last = 0x00EB
            continue
        last = kind
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
        drawing = b''
        objects = []
        previous = None
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
            if kind == 0x00EC or (kind == 0x003C and previous == 0x00EC):  # MSODRAWING and its CONTINUE records.
                drawing += rec
                previous = 0x00EC
                continue
            previous = kind
            if kind == 0x005D and len(rec) >= 10 and struct.unpack_from('<H', rec, 0)[0] == 0x15:
                obj_type, obj_flags = struct.unpack_from('<H', rec, 4)[0], struct.unpack_from('<H', rec, 8)[0]
                objects.append((len(drawing), obj_type, bool(obj_flags & 0x10), obj_type == PICTURE and has_subrecord(rec, 0x0009)))
            elif kind == 0x001C:
                add('Comment')
            elif kind in (0x01B1, 0x087A):  # CF, CF12
                add('ConditionalFormatting')
            elif kind == 0x00B0:
                add('PivotTable')
        # Each shape's OBJ is the first after its client data; the objects not drawn are counted by their kind.
        blips = blip_types(group)
        claimed = {}
        next_object = 0
        for shape in shapes(drawing, 0, len(drawing), []):
            if shape['end'] is None:
                continue
            while next_object < len(objects) and objects[next_object][0] < shape['end']:
                next_object += 1
            if next_object < len(objects):
                claimed[next_object] = shape
                next_object += 1
        for index, (_, obj_type, prints, embedded) in enumerate(objects):
            if not prints or obj_type in (GROUP, CHART, NOTE):
                continue
            shape = claimed.get(index)
            if shape is not None and obj_type == PICTURE and not embedded:
                pib = shape['props'].get(0x0104, 0)
                if 1 <= pib <= len(blips) and blips[pib - 1] in DRAWN_BLIPS:
                    continue
            if (shape is not None and obj_type in DRAWN_OBJECTS and shape['type'] in DRAWN_SHAPES
                    and 0x0145 not in shape['props']):
                continue
            add('OleObject' if obj_type == PICTURE and embedded else 'Picture' if obj_type == PICTURE else 'Shape')
        if counts:
            result[name] = counts
    return result


def cell_content(book, content):
    """Adds to the content the cells (that show a value) with rich text and rotated text, as xlrd reads them."""
    for sheet in book.sheets():
        counts = content.setdefault(sheet.name, {})
        for r in range(sheet.nrows):
            for c in range(sheet.ncols):
                cell = sheet.cell(r, c)
                if cell.ctype in (xlrd.XL_CELL_EMPTY, xlrd.XL_CELL_BLANK) or (cell.ctype == xlrd.XL_CELL_TEXT and cell.value == ''):
                    continue
                alignment = book.xf_list[cell.xf_index].alignment
                # Stacked text (rotation 255) and shrink to fit are shown; headers, footers and page breaks are printed.
                for key, present in (('RichText', cell.ctype == xlrd.XL_CELL_TEXT and (r, c) in sheet.rich_text_runlist_map),
                                     ('RotatedText', alignment.rotation not in (0, 255))):
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
    if book.biff_version >= 80:
        file['print'] = biff_print(name)
    expected[name] = file
with open('expected.json', 'w') as f:
    json.dump(expected, f, indent=1, sort_keys=True, ensure_ascii=True)
