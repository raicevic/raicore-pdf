"""Generates ole.msg: an Outlook message whose attachments are embedded OLE objects (attach method 6), one of each
kind the converter takes a file out of, plus one it can't; and ole-expected.json with what each should give (file
name, size, SHA-256; for the Word 97 storage, its streams). Uses make-msg.py's message writer and ../Cfb/make-cfb.py.
Run with a venv that has olefile, extract-msg and compressed_rtf; from this folder: python make-ole.py"""
import hashlib
import importlib.util
import io
import json
import os
import struct
import zipfile

here = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location('cfb', os.path.join(here, '..', 'Cfb', 'make-cfb.py'))
cfb = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cfb)

# make-msg.py writes its own fixtures when run; take only its functions.
source = open(os.path.join(here, 'make-msg.py')).read().split('\nMESSAGES = {')[0]
msg = {'__file__': os.path.join(here, 'make-msg.py'), '__name__': 'make_msg'}
exec(compile(source, 'make-msg.py', 'exec'), msg)
UNICODE, LONG, OBJECT = msg['UNICODE'], msg['LONG'], msg['OBJECT']


def comp_obj(user_type, prog_id):
    """A \\x01CompObj stream ([MS-OLEDS] 2.3.8): header, user type, clipboard format (none), program id."""
    def ansi(text):
        raw = text.encode('latin-1') + b'\0'
        return struct.pack('<I', len(raw)) + raw
    return struct.pack('<IIi16s', 0xFFFE0001, 0x0A03, -1, b'\0' * 16) + ansi(user_type) + struct.pack('<I', 0) + ansi(prog_id)


def ole10native(label, path, data):
    """A packaged file ([MS-OLEDS] 2.3.6)."""
    body = struct.pack('<H', 2) + label.encode('latin-1') + b'\0' + path.encode('latin-1') + b'\0'
    temp = ('C:\\Temp\\' + label).encode('latin-1') + b'\0'
    body += struct.pack('<II', 0x00030000, len(temp)) + temp + struct.pack('<I', len(data)) + data
    return struct.pack('<I', len(body)) + body


def ole_pres_dib(width, height):
    """An OLE presentation stream ([MS-OLEDS] 2.3.4) with a 24-bit device-independent bitmap (CF_DIB)."""
    row = bytes([0, 0, 255] * width) + b'\0' * ((4 - (3 * width) % 4) % 4)  # Red pixels, rows padded to 4 bytes.
    dib = struct.pack('<IiiHHIIiiII', 40, width, height, 1, 24, 0, len(row) * height, 2835, 2835, 0, 0) + row * height
    return (struct.pack('<iI', -1, 8) + struct.pack('<I', 4)  # Clipboard format CF_DIB; no target device.
            + struct.pack('<IiIIII', 1, -1, 2, 0, width * 26, height * 26) + struct.pack('<I', len(dib)) + dib)


def docx():
    out = io.BytesIO()
    with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as z:
        z.writestr('[Content_Types].xml', '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>')
        z.writestr('word/document.xml', '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"/>')
    return out.getvalue()


TEXT = b'Budget 2026\r\nTravel: 1200\r\n'
PDF = b'%PDF-1.4\n1 0 obj<</Type/Catalog>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n'
WORD = {
    'WordDocument': bytes(range(256)) * 20,  # 5120 bytes: a large stream, in regular sectors.
    '1Table': b'table stream',  # A small one, in the mini stream.
    'Data': bytes(range(250)) * 6,  # 1500 bytes: still under the 4096-byte cutoff, so in the mini stream too.
    '\x01CompObj': comp_obj('Microsoft Word 97-2003 Document', 'Word.Document.8'),
    'ObjectPool': {'_1234': {'\x01Ole': b'\x01\x00\x00\x02' + b'\0' * 16}},  # Nested storages.
}
OBJECTS = [
    ('budget.txt', {'\x01Ole10Native': ole10native('budget.txt', 'C:\\Users\\ada\\budget.txt', TEXT),
                    '\x01CompObj': comp_obj('Package', 'Package')}),
    ('Report', {'Package': docx(), '\x01CompObj': comp_obj('Microsoft Word Document', 'Word.Document.12')}),
    ('Scan', {'CONTENTS': PDF, '\x01CompObj': comp_obj('Adobe Acrobat Document', 'AcroExch.Document.DC')}),
    ('Letter', WORD),
    ('Sheet', {'Workbook': b'\x09\x08' + b'\0' * 30, '\x01CompObj': comp_obj('Microsoft Excel 97-2003 Worksheet', 'Excel.Sheet.8')}),
    ('Slides', {'PowerPoint Document': b'\0' * 40, 'Current User': b'\0' * 20}),
    ('Drawing', {'\x02OlePres000': ole_pres_dib(3, 2), '\x01CompObj': comp_obj('Visio Drawing', 'Visio.Drawing.15')}),
    ('Equation', {'\x01CompObj': comp_obj('Microsoft Equation 3.0', 'Equation.3'), 'Equation Native': b'\0' * 28}),
]

def with_objects(m, objects, embedded=False, extra=None):
    """A message (see make-msg.py) whose attachments are the OLE objects, plus extra attachment storages."""
    tree = msg['message'](m, embedded=embedded)
    children = {}
    for i, (name, ole) in enumerate(objects):
        children['__attach_version1.0_#%08X' % i] = msg['storage'](
            [(0x3705, LONG, 6), (0x3707, UNICODE, name), (0x3001, UNICODE, name), (0x3701, OBJECT, None)],
            b'\0' * 8, children={'__substg1.0_3701000D': ole})
    for j, storage in enumerate(extra or []):
        children['__attach_version1.0_#%08X' % (len(objects) + j)] = storage
    count = len(children)
    header = struct.pack('<8xIIII', 1, count, 1, count) + (b'' if embedded else b'\0' * 8)
    tree['__properties_version1.0'] = header + tree['__properties_version1.0'][len(header):]
    tree.update(children)
    return tree


# An attached message with an object of its own that can't be taken out: reported like the outer one's.
inner = with_objects({
    'name': 'inner', 'subject': 'Inner message', 'from': ('Emmy Noether', 'emmy@example.de'),
    'recipients': [(1, 'Ada Lovelace', 'ada@example.com')], 'text': 'An equation inside.',
}, [('Inner equation', OBJECTS[-1][1])], embedded=True)
forwarded = msg['storage']([(0x3705, LONG, 5), (0x3001, UNICODE, 'Inner message'), (0x3701, OBJECT, None)],
                           b'\0' * 8, children={'__substg1.0_3701000D': inner})
message = with_objects({
    'name': 'ole', 'subject': 'Embedded objects',
    'from': ('Ada Lovelace', 'ada@example.com'),
    'recipients': [(1, 'Charles Babbage', 'charles@example.com')],
    'text': 'Embedded objects are attached.',
}, OBJECTS, extra=[forwarded])
cfb.write_cfb('ole.msg', 3, message)


def digest(data):
    return {'size': len(data), 'sha256': hashlib.sha256(data).hexdigest()}


def streams(tree, prefix=''):
    out = {}
    for name, content in tree.items():
        if isinstance(content, dict):
            out.update(streams(content, prefix + name + '/'))
        else:
            out[prefix + name] = hashlib.sha256(content).hexdigest()
    return out


expected = {
    'attachments': [
        {'name': 'budget.txt', **digest(TEXT)},
        {'name': 'Report.docx', **digest(OBJECTS[1][1]['Package'])},
        {'name': 'Scan.pdf', **digest(PDF)},
        {'name': 'Letter.doc', 'streams': streams(WORD)},
        {'name': 'Sheet.xls', 'streams': streams(OBJECTS[4][1])},
        {'name': 'Slides.ppt', 'streams': streams(OBJECTS[5][1])},
        {'name': 'Drawing.bmp', 'dib': digest(OBJECTS[6][1]['\x02OlePres000'][40:])},
    ],
    'left_out': ['Equation', 'Inner equation'],
}
with open('ole-expected.json', 'w') as f:
    json.dump(expected, f, indent=2, sort_keys=True)

# The message reads back with an independent reader.
import olefile  # noqa: E402
ole = olefile.OleFileIO('ole.msg')
print(sorted('/'.join(e) for e in ole.listdir() if e[0].startswith('__attach') and len(e) <= 3)[:8])
