"""Records in expected.json the drawings of the workbooks with pictures, shapes and charts, as read here independently
of the converter, for SheetDrawingTests.

For each Excel workbook (.xlsx), each worksheet and chart sheet part with a drawing: its anchors in order, as this script parses the
drawing part (zipfile and ElementTree): the anchor's kind, its object (pic, sp, cxnSp, grpSp, graphicFrame,
contentPart), the from and to markers (column, column offset, row, row offset in EMUs), the extent and position (EMUs),
and whether the converter is expected to draw it (it prints, is not hidden, and is a picture of an embedded image in a
format that can be read, a shape, a connector, a group or a chart). openpyxl, an independent reader, reads the same
workbook: the anchors of the pictures and charts it finds must be among them (checked here), and their numbers are
recorded, with the workbook's visible sheets in order and which of them are chart sheets.

For each Excel 97-2003 workbook (.xls), each worksheet with a drawing: its shapes in order, as this script walks the
OfficeArt data of the sheet's MSODRAWING records (with olefile): the OBJ record's type that follows each shape's client
data, the shape type, the client anchor (column, offset in 1024ths of the column, row, offset in 256ths of the row, for
the top-left and bottom-right corners) and whether it is expected to be drawn (it prints, and is a picture whose BLIP is
PNG, JPEG, DIB, TIFF, WMF or EMF, or a line, rectangle, oval or text box of a common shape type).

Run with a venv that has openpyxl 3.1, pillow and olefile; from this folder: python make-expected.py"""
import json
import posixpath
import struct
import warnings
import xml.etree.ElementTree as ET
import zipfile

import olefile
import openpyxl

FILES = [
    'picture-and-shape-same-size.xlsx', 'absolute-anchor-over-empty-sheet.xlsx', 'WithTextBox.xlsx', 'WithThreeCharts.xlsx',
    '../../Workbooks/Poi/WithDrawing.xlsx', '../content.xlsx', 'WithChartSheet.xlsx', '../../Workbooks/Poi/chart_sheet.xlsx',
    'SimpleWithImages.xls', 'SheetWithDrawing.xls', '../Xls/DrawingAndComments.xls', '../Xls/46250.xls', '../Xls/44010-SingleChart.xls',
]
IMAGE_TYPES = ('.png', '.jpeg', '.jpg', '.gif', '.bmp', '.tif', '.tiff', '.wmf', '.emf')
OBJECTS = ('pic', 'sp', 'cxnSp', 'grpSp', 'graphicFrame', 'contentPart')


def local(tag):
    return tag.rsplit('}', 1)[-1]


def child(element, name):
    if element is None:
        return None
    return next((c for c in element if local(c.tag) == name), None)


def relationships(zip_file, part):
    folder, name = posixpath.split(part)
    path = posixpath.join(folder, '_rels', name + '.rels')
    if path not in zip_file.namelist():
        return {}
    result = {}
    for rel in ET.fromstring(zip_file.read(path)):
        if rel.get('TargetMode') != 'External':
            result[rel.get('Id')] = (rel.get('Type').rsplit('/', 1)[-1], posixpath.normpath(posixpath.join(folder, rel.get('Target'))))
    return result


def marker(element):
    if element is None:
        return None
    return [int(child(element, n).text) for n in ('col', 'colOff', 'row', 'rowOff')]


def choices(element):
    """The children with markup compatibility resolved: an AlternateContent stands for its first Choice's children."""
    for c in element:
        if local(c.tag) == 'AlternateContent':
            choice = child(c, 'Choice')
            if choice is not None:
                yield from choices(choice)
        else:
            yield c


def drawn(obj, rels):
    nv = next((c for c in obj if local(c.tag).startswith('nv')), None)
    info = child(nv, 'cNvPr')
    if info is not None and info.get('hidden') in ('1', 'true'):
        return False
    kind = local(obj.tag)
    if kind == 'pic':
        blip = child(child(obj, 'blipFill'), 'blip')
        rid = None if blip is None else next((v for k, v in blip.attrib.items() if local(k) == 'embed'), None)
        return rid in rels and rels[rid][1].lower().endswith(IMAGE_TYPES)
    if kind == 'graphicFrame':
        data = child(child(obj, 'graphic'), 'graphicData')
        return data is not None and data.get('uri', '').endswith('/chart')
    return kind in ('sp', 'cxnSp', 'grpSp')


def xlsx(path):
    result = {}
    with zipfile.ZipFile(path) as z:
        for part in sorted(n for n in z.namelist() if n.startswith(('xl/worksheets/sheet', 'xl/chartsheets/sheet')) and n.endswith('.xml')):
            for kind, target in relationships(z, part).values():
                if kind != 'drawing':
                    continue
                rels = relationships(z, target)
                anchors = []
                for anchor in choices(ET.fromstring(z.read(target))):
                    name = local(anchor.tag)
                    if name not in ('twoCellAnchor', 'oneCellAnchor', 'absoluteAnchor'):
                        continue
                    obj = next((c for c in choices(anchor) if local(c.tag) in OBJECTS), None)
                    if obj is None:
                        continue
                    data = child(anchor, 'clientData')
                    ext, pos = child(anchor, 'ext'), child(anchor, 'pos')
                    anchors.append({
                        'anchor': name,
                        'object': local(obj.tag),
                        'from': marker(child(anchor, 'from')),
                        'to': marker(child(anchor, 'to')),
                        'ext': None if ext is None else [int(ext.get('cx')), int(ext.get('cy'))],
                        'pos': None if pos is None else [int(pos.get('x')), int(pos.get('y'))],
                        'drawn': drawn(obj, rels) and not (data is not None and data.get('fPrintsWithSheet') in ('0', 'false')),
                    })
                result[part] = anchors
    return result


def openpyxl_check(path, sheets):
    """The pictures and charts openpyxl finds; each one's anchor must be one of those parsed here."""
    with warnings.catch_warnings():
        warnings.simplefilter('ignore')
        book = openpyxl.load_workbook(path)
    found = {'images': 0, 'charts': 0,
             'visible': [ws.title for ws in book._sheets if ws.sheet_state == 'visible'],
             'chartsheets': [cs.title for cs in book.chartsheets]}
    parsed = [a for anchors in sheets.values() for a in anchors]
    for ws in book.worksheets + book.chartsheets:
        for key, items in (('images', getattr(ws, '_images', [])), ('charts', ws._charts)):
            for item in items:
                found[key] += 1
                a = item.anchor
                start = [a._from.col, a._from.colOff, a._from.row, a._from.rowOff] if hasattr(a, '_from') else None
                assert any(p['from'] == start or (start is None and p['anchor'] == 'absoluteAnchor') for p in parsed), (path, start)
    return found


def officeart(data, start, end):
    position = start
    while position + 8 <= end:
        vi, kind, length = struct.unpack_from('<HHI', data, position)
        body_end = min(position + 8 + length, end)
        yield kind, vi >> 4, vi & 0xF, position + 8, body_end
        position = body_end


def records(data, position):
    while position + 4 <= len(data):
        kind, length = struct.unpack_from('<HH', data, position)
        yield kind, data[position + 4:position + 4 + length]
        position += 4 + length


def shapes(data, start, end, found):
    for kind, _, version, s, e in officeart(data, start, end):
        if kind == 0xF004:
            shape = {'end': None, 'type': None, 'props': {}, 'anchor': None, 'patriarch': False}
            for inner, instance, _, bs, be in officeart(data, s, e):
                if inner == 0xF00A and be - bs >= 8:
                    shape['type'] = instance
                    shape['patriarch'] = bool(struct.unpack_from('<I', data, bs + 4)[0] & 0x4)
                elif inner == 0xF00B:
                    for i in range(min(instance, (be - bs) // 6)):
                        pid, value = struct.unpack_from('<Hi', data, bs + 6 * i)
                        shape['props'].setdefault(pid & 0x3FFF, value)
                elif inner == 0xF010 and be - bs >= 18:
                    shape['anchor'] = list(struct.unpack_from('<8H', data, bs + 2))
                elif inner == 0xF011:
                    shape['end'] = be
            if not shape['patriarch']:
                found.append(shape)
        elif version == 0xF:
            shapes(data, s, e, found)
    return found


def blips(group):
    result = []
    for kind, _, _, start, end in officeart(group, 0, len(group)):
        for inner, _, _, s, e in officeart(group, start, end) if kind == 0xF000 else []:
            for _, _, _, fs, fe in officeart(group, s, e) if inner == 0xF001 else []:
                name = group[fs + 33] if fs + 33 < fe else 0
                blip = fs + 36 + name
                result.append(struct.unpack_from('<H', group, blip + 2)[0] if blip + 8 <= fe else None)
    return result


def xls(path):
    ole = olefile.OleFileIO(path)
    data = ole.openstream('Workbook').read()
    sheets, group, last = [], b'', None
    for kind, rec in records(data, 0):
        if kind == 0x00EB or (kind == 0x003C and last == 0x00EB):
            group += rec
            last = 0x00EB
            continue
        last = kind
        if kind == 0x0085:
            length, flags = rec[6], rec[7]
            name = rec[8:8 + 2 * length].decode('utf-16-le') if flags & 1 else rec[8:8 + length].decode('latin-1')
            sheets.append((name, struct.unpack_from('<I', rec, 0)[0]))
        elif kind == 0x000A:
            break
    store = blips(group)
    result = {}
    for name, offset in sheets:
        drawing, objects, depth, previous = b'', [], 0, None
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
            if kind == 0x00EC or (kind == 0x003C and previous == 0x00EC):
                drawing += rec
                previous = 0x00EC
                continue
            previous = kind
            if kind == 0x005D and len(rec) >= 10 and struct.unpack_from('<H', rec, 0)[0] == 0x15:
                objects.append((len(drawing), struct.unpack_from('<H', rec, 4)[0], bool(struct.unpack_from('<H', rec, 8)[0] & 0x10)))
        if not drawing:
            continue
        found, next_object = [], 0
        for shape in shapes(drawing, 0, len(drawing), []):
            if shape['end'] is None or shape['anchor'] is None:
                continue
            while next_object < len(objects) and objects[next_object][0] < shape['end']:
                next_object += 1
            if next_object >= len(objects):
                break
            _, obj_type, prints = objects[next_object]
            next_object += 1
            pib = shape['props'].get(0x0104, 0)
            picture = obj_type == 0x08 and 1 <= pib <= len(store) and store[pib - 1] in (0xF01A, 0xF01B, 0xF01D, 0xF01E, 0xF01F, 0xF029, 0xF02A)
            simple = obj_type in (0x01, 0x02, 0x03, 0x06, 0x1E) and shape['type'] in (1, 2, 3, 4, 5, 6, 20, 32, 202) and 0x0145 not in shape['props']
            found.append({'object': obj_type, 'shape': shape['type'], 'anchor': shape['anchor'],
                          'drawn': prints and obj_type != 0x19 and (picture or simple)})
        result[name] = found
    return result


expected = {}
for path in FILES:
    if path.endswith('.xlsx'):
        sheets = xlsx(path)
        expected[path] = {'sheets': sheets, 'openpyxl': openpyxl_check(path, sheets)}
    else:
        expected[path] = {'sheets': xls(path)}

with open('expected.json', 'w') as f:
    json.dump(expected, f, indent=1, sort_keys=True)
