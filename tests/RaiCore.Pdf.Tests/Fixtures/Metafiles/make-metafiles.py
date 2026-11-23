"""Writes the WMF and EMF test pictures, whose geometry is known exactly (python3 make-metafiles.py).

Every picture is 200 x 100 points, and one logical unit is one point with y down (WMF: a placeable header at 72 units
per inch; EMF: a 72 dpi reference device in MM_TEXT; EMF+: 72 dpi), so a picture drawn at its natural size and
rendered at 72 dpi has pixel (x, y) at logical (x, y). MetafileTests.cs checks the drawings by pixel and by text
position.
"""

import os
import struct

HERE = os.path.dirname(os.path.abspath(__file__))


def color(r, g, b):
    return struct.pack('<BBBB', r, g, b, 0)


# ----- WMF ([MS-WMF]) -----

def wmf_record(function, params=b''):
    if len(params) % 2:
        params += b'\0'
    return struct.pack('<IH', 3 + len(params) // 2, function) + params


def words(*values):
    return struct.pack('<' + 'h' * len(values), *values)


def wmf(records, objects, box=(0, 0, 200, 100), inch=72):
    body = b''.join(records) + wmf_record(0x0000)
    placeable = struct.pack('<IHhhhhHI', 0x9AC6CDD7, 0, *box, inch, 0)
    checksum = 0
    for i in range(0, 20, 2):
        checksum ^= struct.unpack_from('<H', placeable, i)[0]
    placeable += struct.pack('<H', checksum)
    size = (18 + len(body)) // 2
    largest = max(struct.unpack_from('<I', body, i)[0] for i in record_offsets(body))
    header = struct.pack('<HHHIHIH', 1, 9, 0x0300, size, objects, largest, 0)
    return placeable + header + body


def record_offsets(body):
    offset = 0
    while offset < len(body):
        yield offset
        offset += struct.unpack_from('<I', body, offset)[0] * 2


def dib(width, height, rows_bottom_up):
    """A 24-bit BITMAPINFOHEADER and its rows (bottom row first), each pixel (r, g, b)."""
    info = struct.pack('<IiiHHIIiiII', 40, width, height, 1, 24, 0, 0, 0, 0, 0, 0)
    bits = b''
    for row in rows_bottom_up:
        line = b''.join(struct.pack('<BBB', b, g, r) for r, g, b in row)
        bits += line + b'\0' * (-len(line) % 4)
    return info, bits


def font_wmf(height, face, weight=400, charset=0):
    name = face.encode('latin-1')[:31] + b'\0'
    return words(height, 0, 0, 0, weight) + struct.pack('<BBBBBBBB', 0, 0, 0, charset, 0, 0, 0, 0) + name


def make_shapes_wmf():
    r = []
    r.append(wmf_record(0x020B, words(0, 0)))                                # SetWindowOrg (y, x)
    r.append(wmf_record(0x020C, words(100, 200)))                            # SetWindowExt (y, x)
    r.append(wmf_record(0x02FA, struct.pack('<Hhh', 5, 0, 0) + color(0, 0, 0)))       # 0: null pen
    r.append(wmf_record(0x012D, words(0)))
    r.append(wmf_record(0x02FC, struct.pack('<H', 0) + color(255, 0, 0) + struct.pack('<H', 0)))  # 1: red brush
    r.append(wmf_record(0x012D, words(1)))
    r.append(wmf_record(0x041B, words(40, 40, 10, 10)))                      # Rectangle (bottom, right, top, left)
    r.append(wmf_record(0x02FC, struct.pack('<H', 0) + color(0, 0, 255) + struct.pack('<H', 0)))  # 2: blue brush
    r.append(wmf_record(0x012D, words(2)))
    r.append(wmf_record(0x0418, words(40, 80, 10, 50)))                      # Ellipse
    r.append(wmf_record(0x02FC, struct.pack('<H', 0) + color(0, 160, 0) + struct.pack('<H', 0)))  # 3: green brush
    r.append(wmf_record(0x012D, words(3)))
    r.append(wmf_record(0x0324, words(3, 90, 40, 105, 10, 120, 40)))         # Polygon
    r.append(wmf_record(0x0102, words(1)))                                   # SetBkMode transparent
    r.append(wmf_record(0x02FC, struct.pack('<H', 2) + color(0, 0, 0) + struct.pack('<H', 4)))    # 4: cross hatch
    r.append(wmf_record(0x012D, words(4)))
    r.append(wmf_record(0x041B, words(40, 160, 10, 130)))
    r.append(wmf_record(0x02FA, struct.pack('<Hhh', 0x0200, 6, 0) + color(0, 0, 0)))  # 5: 6 wide, flat caps
    r.append(wmf_record(0x012D, words(5)))
    r.append(wmf_record(0x0214, words(25, 170)))                             # MoveTo (y, x)
    r.append(wmf_record(0x0213, words(25, 195)))                             # LineTo
    r.append(wmf_record(0x012D, words(0)))                                   # The null pen again.
    r.append(wmf_record(0x02FB, font_wmf(-12, 'Arial')))                     # 6: font
    r.append(wmf_record(0x012D, words(6)))
    r.append(wmf_record(0x012E, words(24)))                                  # SetTextAlign baseline
    r.append(wmf_record(0x0209, color(0, 0, 0)))                             # SetTextColor
    text = b'Metafile'
    r.append(wmf_record(0x0521, words(len(text)) + text + words(70, 10)))    # TextOut (length, text, y, x)
    text = b'AB'
    r.append(wmf_record(0x0A32, words(70, 60, len(text), 0) + text + words(40, 10)))  # ExtTextOut with dx
    r.append(wmf_record(0x001E))                                             # SaveDC
    r.append(wmf_record(0x0416, words(90, 160, 50, 130)))                    # IntersectClipRect
    r.append(wmf_record(0x02FC, struct.pack('<H', 0) + color(255, 255, 0) + struct.pack('<H', 0)))  # 7: yellow
    r.append(wmf_record(0x012D, words(7)))
    r.append(wmf_record(0x041B, words(95, 170, 50, 120)))
    r.append(wmf_record(0x0127, words(-1)))                                  # RestoreDC
    r.append(wmf_record(0x02FC, struct.pack('<H', 0) + color(255, 128, 0) + struct.pack('<H', 0)))  # 8: orange
    r.append(wmf_record(0x012D, words(8)))
    r.append(wmf_record(0x041B, words(95, 195, 50, 175)))
    info, bits = dib(2, 1, [[(255, 0, 0), (0, 0, 255)]])
    r.append(wmf_record(0x0F43, struct.pack('<IH', 0x00CC0020, 0) + words(1, 2, 0, 0, 15, 20, 80, 60) + info + bits))  # StretchDIB
    return wmf(r, 9)


# ----- EMF ([MS-EMF]) -----

def emf_record(kind, data=b''):
    data += b'\0' * (-len(data) % 4)
    return struct.pack('<II', kind, 8 + len(data)) + data


def emf(records, handles, plus=False):
    # A 72 dpi reference device: 720 pixels in 254 mm (and 254000 micrometers).
    frame = (0, 0, round(200 * 2540 / 72), round(100 * 2540 / 72))
    body = b''.join(records) + emf_record(14, struct.pack('<III', 0, 16, 20))
    count = len(list(emf_offsets(body))) + 1
    description = 'make-metafiles\0\0'.encode('utf-16-le')
    size = 108 + len(description)
    header = struct.pack('<II4i4iIIIIHHIIIiiii', 1, size, 0, 0, 200, 100, *frame, 0x464D4520, 0x10000,
                         size + len(body), count, handles, 0, len(description) // 2, 108, 0, 720, 720, 254, 254)
    header += struct.pack('<IIIii', 0, 0, 0, 254000, 254000) + description
    return header + body


def emf_offsets(body):
    offset = 0
    while offset < len(body):
        yield offset
        offset += struct.unpack_from('<I', body, offset + 4)[0]


def rect32(left, top, right, bottom):
    return struct.pack('<4i', left, top, right, bottom)


def logfont(height, face, weight=400):
    return struct.pack('<5i8B', height, 0, 0, 0, weight, 0, 0, 0, 0, 0, 0, 0, 0) + face.encode('utf-16-le').ljust(64, b'\0')


def brush(index, r, g, b, style=0, hatch=0):
    return emf_record(39, struct.pack('<II', index, style) + color(r, g, b) + struct.pack('<I', hatch))


def select(index):
    return emf_record(37, struct.pack('<I', index))


def text_w(x, y, text, dx=None, options=0, clip=(0, 0, -1, -1)):
    units = text.encode('utf-16-le')
    count = len(units) // 2
    off_string = 8 + 16 + 12 + 40
    off_dx = off_string + len(units) + (-len(units) % 4)
    emrtext = struct.pack('<iiIII', x, y, count, off_string, options) + rect32(*clip) + struct.pack('<I', off_dx if dx else 0)
    data = rect32(0, 0, -1, -1) + struct.pack('<Iff', 1, 0, 0) + emrtext + units + b'\0' * (-len(units) % 4)
    if dx:
        data += struct.pack('<' + 'i' * count, *dx)
    return emf_record(84, data)


NULL_PEN = 0x80000008


def make_shapes_emf():
    r = []
    r.append(select(NULL_PEN))
    r.append(brush(1, 255, 0, 0))
    r.append(select(1))
    r.append(emf_record(43, rect32(10, 10, 40, 40)))                         # Rectangle
    # A world transform turning the x axis down: (x, y) -> (80 - y, 10 + x).
    r.append(emf_record(33))                                                 # SaveDC
    r.append(emf_record(35, struct.pack('<6f', 0, 1, -1, 0, 80, 10)))        # SetWorldTransform
    r.append(brush(2, 0, 0, 255))
    r.append(select(2))
    r.append(emf_record(43, rect32(0, 0, 30, 10)))
    r.append(emf_record(34, struct.pack('<i', -1)))                          # RestoreDC
    # A path: a triangle, filled.
    r.append(brush(3, 0, 160, 0))
    r.append(select(3))
    r.append(emf_record(59))                                                 # BeginPath
    r.append(emf_record(27, struct.pack('<ii', 90, 10)))                     # MoveToEx
    r.append(emf_record(54, struct.pack('<ii', 120, 10)))                    # LineTo
    r.append(emf_record(54, struct.pack('<ii', 105, 40)))
    r.append(emf_record(61))                                                 # CloseFigure
    r.append(emf_record(60))                                                 # EndPath
    r.append(emf_record(62, rect32(0, 0, 0, 0)))                             # FillPath
    # A clip path (a circle), then a square filled through it.
    r.append(emf_record(33))
    r.append(emf_record(59))
    r.append(emf_record(42, rect32(130, 10, 160, 40)))                       # Ellipse
    r.append(emf_record(60))
    r.append(emf_record(67, struct.pack('<I', 5)))                           # SelectClipPath RGN_COPY
    r.append(brush(4, 255, 0, 255))
    r.append(select(4))
    r.append(emf_record(43, rect32(125, 5, 165, 45)))
    r.append(emf_record(34, struct.pack('<i', -1)))
    # MM_ANISOTROPIC: a 10 x 10 window on a 30 x 30 viewport at (165, 10).
    r.append(emf_record(33))
    r.append(emf_record(17, struct.pack('<I', 8)))                           # SetMapMode
    r.append(emf_record(9, struct.pack('<ii', 10, 10)))                      # SetWindowExtEx
    r.append(emf_record(11, struct.pack('<ii', 30, 30)))                     # SetViewportExtEx
    r.append(emf_record(12, struct.pack('<ii', 165, 10)))                    # SetViewportOrgEx
    r.append(brush(5, 0, 255, 255))
    r.append(select(5))
    r.append(emf_record(43, rect32(0, 0, 10, 10)))
    r.append(emf_record(34, struct.pack('<i', -1)))
    # MM_LOMETRIC (0.1 mm, y up) from (165, 95): 10 mm squares are 28.35 points.
    r.append(emf_record(33))
    r.append(emf_record(17, struct.pack('<I', 2)))
    r.append(emf_record(12, struct.pack('<ii', 165, 95)))
    r.append(brush(6, 0, 100, 0))
    r.append(select(6))
    r.append(emf_record(43, rect32(0, 0, 100, 100)))
    r.append(emf_record(34, struct.pack('<i', -1)))
    # A dashed geometric pen with flat caps: 8 on, 8 off, 4 wide.
    style = 0x10000 | 0x200 | 7                                              # Geometric, flat caps, user style.
    r.append(emf_record(95, struct.pack('<IIIII', 7, 0, 0, 0, 0) + struct.pack('<IIIIII', style, 4, 0, 0, 0, 2) + struct.pack('<II', 8, 8)))
    r.append(select(7))
    r.append(emf_record(27, struct.pack('<ii', 40, 50)))
    r.append(emf_record(54, struct.pack('<ii', 90, 50)))
    r.append(select(NULL_PEN))
    # Text: baseline at y = 70, with advances; then text on an opaque background box.
    r.append(emf_record(82, struct.pack('<I', 8) + logfont(-12, 'Arial')))  # ExtCreateFontIndirectW
    r.append(select(8))
    r.append(emf_record(22, struct.pack('<I', 24)))                          # SetTextAlign baseline
    r.append(emf_record(24, color(0, 0, 0)))                                 # SetTextColor
    r.append(emf_record(18, struct.pack('<I', 1)))                           # SetBkMode transparent
    r.append(text_w(10, 70, 'Enhanced', [7] * 8))
    r.append(emf_record(25, color(128, 128, 128)))                           # SetBkColor
    r.append(text_w(100, 70, 'Box', None, options=2, clip=(100, 58, 160, 75)))  # ETO_OPAQUE
    # A 2 x 2 bottom-up DIB: blue, white above red, green.
    info, bits = dib(2, 2, [[(255, 0, 0), (0, 255, 0)], [(0, 0, 255), (255, 255, 255)]])
    head = rect32(0, 0, 0, 0) + struct.pack('<6i', 100, 80, 0, 0, 2, 2)
    offset = 8 + len(head) + 4 * 4 + 4 + 4 + 8
    head += struct.pack('<IIII', offset, len(info), offset + len(info), len(bits)) + struct.pack('<II', 0, 0x00CC0020) + struct.pack('<ii', 20, 20)
    r.append(emf_record(81, head + info + bits))                             # StretchDIBits
    return emf(r, 9)


# ----- EMF+ ([MS-EMFPLUS]) -----

def plus(kind, flags=0, data=b''):
    data += b'\0' * (-len(data) % 4)
    return struct.pack('<HHII', kind, flags, 12 + len(data), len(data)) + data


def comment(*records):
    data = struct.pack('<I', 0x2B464D45) + b''.join(records)
    return emf_record(70, struct.pack('<I', len(data)) + data)


def argb(a, r, g, b):
    return struct.pack('<BBBB', b, g, r, a)


VERSION = 0xDBC01002


def plus_header(dual):
    return plus(0x4001, 1 if dual else 0, struct.pack('<IIII', VERSION, 0, 72, 72))


def plus_records(extra=b''):
    out = []
    out.append(comment(plus_header(dual=True), plus(0x4030, 2, struct.pack('<f', 1))))  # Page unit: pixels.
    out.append(comment(plus(0x400A, 0x8000, argb(255, 255, 0, 0) + struct.pack('<I', 1) + struct.pack('<4f', 10, 10, 20, 20))))
    # EMF records that duplicate the drawing (a dual-mode file): skipped.
    out.append(brush(1, 255, 0, 255))
    out.append(select(1))
    out.append(emf_record(43, rect32(10, 10, 30, 30)))
    # A linear gradient brush (object 0) from red to blue across x 50 to 90.
    gradient = struct.pack('<II', VERSION, 4) + struct.pack('<Ii4f', 0, 0, 50, 10, 40, 30) + argb(255, 255, 0, 0) + argb(255, 0, 0, 255) + struct.pack('<II', 0, 0)
    out.append(comment(plus(0x4008, 0x0100, gradient), plus(0x400A, 0, struct.pack('<II', 0, 1) + struct.pack('<4f', 50, 10, 40, 30))))
    # A pen (object 1): 4 wide, solid blue; a line from (110, 25) to (150, 25).
    pen = struct.pack('<II', VERSION, 0) + struct.pack('<IIf', 0, 2, 4) + struct.pack('<II', VERSION, 0) + argb(255, 0, 0, 255)
    out.append(comment(plus(0x4008, 0x0201, pen), plus(0x400D, 0x0001, struct.pack('<I', 2) + struct.pack('<4f', 110, 25, 150, 25))))
    # A path (object 2): a triangle, filled green.
    path = struct.pack('<III', VERSION, 3, 0) + struct.pack('<6f', 160, 40, 175, 10, 190, 40) + bytes([0, 1, 0x81]) + b'\0'
    out.append(comment(plus(0x4008, 0x0302, path), plus(0x4014, 0x8002, argb(255, 0, 160, 0))))
    # A font (object 3): Arial, 12 points; a string at (10, 55).
    family = 'Arial'.encode('utf-16-le')
    font = struct.pack('<IfIiII', VERSION, 12, 3, 0, 0, len(family) // 2) + family
    text = 'Plus text'.encode('utf-16-le')
    out.append(comment(plus(0x4008, 0x0603, font), plus(0x401C, 0x8003, argb(255, 0, 0, 0) + struct.pack('<II', 0xFFFFFFFF, len(text) // 2) + struct.pack('<4f', 10, 55, 0, 0) + text)))
    # A translucent ellipse through a world transform: (0, 0, 30, 30) moved to (110, 50).
    out.append(comment(plus(0x402A, 0, struct.pack('<6f', 1, 0, 0, 1, 110, 50)),
                       plus(0x400E, 0x8000, argb(128, 0, 255, 0) + struct.pack('<4f', 0, 0, 30, 30)),
                       plus(0x402B)) + extra)
    # GetDC: the EMF records after it are drawn.
    out.append(comment(plus(0x4004)))
    out.append(brush(2, 0, 255, 255))
    out.append(select(2))
    out.append(emf_record(43, rect32(160, 60, 190, 90)))
    out.append(comment(plus(0x4002)))
    return out


def make_plus_emf():
    return emf(plus_records(), 3)


def make_plus_fallback_emf():
    # An EMF+ record the player does not draw (a serializable object): the file is drawn from its EMF records.
    return emf(plus_records(comment(plus(0x4038, 0, b'\0' * 20))), 3)


def make_plus_only_emf():
    r = [comment(plus(0x4001, 0, struct.pack('<IIII', VERSION, 0, 72, 72)),
                 plus(0x400A, 0x8000, argb(255, 255, 0, 0) + struct.pack('<I', 1) + struct.pack('<4f', 10, 10, 20, 20)))]
    r.append(brush(1, 255, 0, 255))
    r.append(select(1))
    r.append(emf_record(43, rect32(50, 10, 70, 30)))
    r.append(comment(plus(0x4002)))
    return emf(r, 2)


def make_arcs_wmf():
    """A window twice the picture's size, its origin at (0, 40): logical (x, y) is pixel (x / 2, (y - 40) / 2)."""
    r = [wmf_record(0x020B, words(40, 0)), wmf_record(0x020C, words(200, 400))]
    r.append(wmf_record(0x02FA, struct.pack('<Hhh', 5, 0, 0) + color(0, 0, 0)))                  # 0: null pen
    r.append(wmf_record(0x012D, words(0)))
    r.append(wmf_record(0x02FC, struct.pack('<H', 0) + color(255, 0, 0) + struct.pack('<H', 0)))  # 1: red
    r.append(wmf_record(0x012D, words(1)))
    # A pie from east to north (counterclockwise): the upper right quarter of the circle at pixels (10, 10)-(90, 90).
    r.append(wmf_record(0x081A, words(60, 100, 140, 180, 220, 180, 60, 20)))  # yEnd, xEnd, yStart, xStart, bottom, right, top, left
    r.append(wmf_record(0x02FB, font_wmf(-24, 'Arial')))                     # 2: 12 points
    r.append(wmf_record(0x012D, words(2)))
    r.append(wmf_record(0x012E, words(2 | 24)))                              # TA_RIGHT | TA_BASELINE
    r.append(wmf_record(0x0521, words(5) + b'Right\0' + words(140, 380)))   # Ends at pixel (190, 50).
    r.append(wmf_record(0x012E, words(24)))
    font = font_wmf(-24, 'Arial')
    font = font[:4] + struct.pack('<h', 900) + font[6:]                      # Escapement: 90 degrees.
    r.append(wmf_record(0x02FB, font))                                       # 3
    r.append(wmf_record(0x012D, words(3)))
    r.append(wmf_record(0x0521, words(2) + b'Up' + words(220, 300)))         # Up from pixel (150, 90).
    r.append(wmf_record(0x02FA, struct.pack('<Hhh', 0x0200, 8, 0) + color(0, 0, 0)))             # 4: 8 wide (4 points), flat caps
    r.append(wmf_record(0x012D, words(4)))
    r.append(wmf_record(0x0214, words(200, 220)))
    r.append(wmf_record(0x0213, words(200, 380)))                            # From pixel (110, 80) to (190, 80).
    r.append(wmf_record(0x02FC, struct.pack('<H', 0) + color(0, 160, 0) + struct.pack('<H', 0)))  # 5: green
    r.append(wmf_record(0x012D, words(5)))
    # DIBBITBLT without a bitmap: PATCOPY with the brush, at pixel (110, 10), 30 x 20.
    r.append(wmf_record(0x0940, struct.pack('<I', 0x00F00021) + words(0, 0, 0, 40, 60, 60, 220)))
    return wmf(r, 6, box=(0, 40, 400, 240), inch=144)


def make_modes_emf():
    r = [select(NULL_PEN)]
    # MM_ISOTROPIC: a 10 x 10 window on a 60 x 30 viewport keeps its aspect: 30 x 30 from (10, 10).
    r += [emf_record(33), emf_record(17, struct.pack('<I', 7)), emf_record(9, struct.pack('<ii', 10, 10)),
          emf_record(11, struct.pack('<ii', 60, 30)), emf_record(12, struct.pack('<ii', 10, 10)),
          brush(1, 255, 0, 0), select(1), emf_record(43, rect32(0, 0, 10, 10)), emf_record(34, struct.pack('<i', -1))]
    # ModifyWorldTransform MWT_LEFTMULTIPLY: scale 2 before a translation by 100: x -> 2x + 100.
    r += [emf_record(33), emf_record(35, struct.pack('<6f', 1, 0, 0, 1, 100, 0)),
          emf_record(36, struct.pack('<6fI', 2, 0, 0, 2, 0, 0, 2)),
          brush(2, 0, 0, 255), select(2), emf_record(43, rect32(0, 10, 10, 20)), emf_record(34, struct.pack('<i', -1))]
    # Two clip rectangles intersect: only x 150 to 170 is drawn.
    r += [emf_record(33), emf_record(30, rect32(130, 10, 170, 40)), emf_record(30, rect32(150, 0, 200, 100)),
          brush(3, 0, 160, 0), select(3), emf_record(43, rect32(130, 10, 190, 40)), emf_record(34, struct.pack('<i', -1))]
    # An excluded rectangle leaves a hole.
    r += [emf_record(33), emf_record(29, rect32(20, 60, 40, 80)),
          brush(4, 255, 0, 255), select(4), emf_record(43, rect32(10, 50, 50, 90)), emf_record(34, struct.pack('<i', -1))]
    # An excluded rectangle in a space with y up (MM_LOMETRIC from (100, 95)): a hole of 10 mm (28.35 points).
    r += [emf_record(33), emf_record(17, struct.pack('<I', 2)), emf_record(12, struct.pack('<ii', 100, 95)),
          emf_record(29, rect32(0, 0, 100, 100)), brush(5, 0, 255, 255), select(5),
          emf_record(43, rect32(-50, 150, 150, -50)), emf_record(34, struct.pack('<i', -1))]
    # A 32-bit bitmap whose fourth bytes are not zero: GDI ignores them, so the blue pixel is opaque.
    info = struct.pack('<IiiHHIIiiII', 40, 1, 1, 1, 32, 0, 0, 0, 0, 0, 0)
    bits = struct.pack('<BBBB', 255, 0, 0, 0x40)
    head = rect32(0, 0, 0, 0) + struct.pack('<6i', 70, 60, 0, 0, 1, 1)
    offset = 8 + len(head) + 4 * 4 + 4 + 4 + 8
    head += struct.pack('<IIII', offset, len(info), offset + len(info), len(bits)) + struct.pack('<II', 0, 0x00CC0020) + struct.pack('<ii', 20, 20)
    r.append(emf_record(81, head + info + bits))
    return emf(r, 6)


def embedded_wmf(emf_data):
    """A WMF that draws "Wmf text" and carries an EMF in two comment chunks, as Office writes them."""
    r = [wmf_record(0x020B, words(0, 0)), wmf_record(0x020C, words(100, 200)),
         wmf_record(0x02FB, font_wmf(-12, 'Arial')), wmf_record(0x012D, words(0)), wmf_record(0x012E, words(24))]
    half = len(emf_data) // 2
    for i, chunk in enumerate([emf_data[:half], emf_data[half:]]):
        remaining = len(emf_data) - half if i == 0 else 0
        data = struct.pack('<IIIHIIIII', 0x43464D57, 1, 0x00010000, 0, 0, 2, len(chunk), remaining, len(emf_data)) + chunk
        r.append(wmf_record(0x0626, struct.pack('<HH', 0x000F, len(data)) + data))
    text = b'Wmf text'
    r.append(wmf_record(0x0521, words(len(text)) + text + words(50, 10)))
    return wmf(r, 1)


def make_glyphs_emf():
    """Text stored as glyph numbers (ETO_GLYPH_INDEX): "Glyphs" in Liberation Sans 2 (its glyphs 42, 79, 92, 83, 75, 86)."""
    r = [emf_record(82, struct.pack('<I', 1) + logfont(-12, 'Liberation Sans')), select(1),
         emf_record(22, struct.pack('<I', 24)), emf_record(18, struct.pack('<I', 1))]
    ids = struct.pack('<6H', 42, 79, 92, 83, 75, 86)
    off_string = 8 + 16 + 12 + 40
    off_dx = off_string + len(ids)
    emrtext = struct.pack('<iiIII', 10, 70, 6, off_string, 0x10) + rect32(0, 0, -1, -1) + struct.pack('<I', off_dx)
    r.append(emf_record(84, rect32(0, 0, -1, -1) + struct.pack('<Iff', 1, 0, 0) + emrtext + ids + struct.pack('<6i', *[10] * 6)))
    return emf(r, 2)


def make_nested_emf(depth):
    """Pictures in pictures: each level draws the next as an EMF+ image filling it; the innermost draws a red square."""
    inner = emf([brush(1, 255, 0, 0), select(1), select(NULL_PEN), emf_record(43, rect32(50, 25, 150, 75))], 2)
    for _ in range(depth):
        image = struct.pack('<II', VERSION, 2) + struct.pack('<II', 3, len(inner)) + inner
        chunks = [comment(plus_header(dual=False), plus(0x4030, 2, struct.pack('<f', 1)), plus(0x4008, 0x0500, image),
                          plus(0x401A, 0x0000, struct.pack('<Ii4f', 0xFFFFFFFF, 2, 0, 0, 200, 100) + struct.pack('<4f', 0, 0, 200, 100))),
                  comment(plus(0x4002))]
        inner = emf(chunks, 1)
    return inner


if __name__ == '__main__':
    files = {
        'shapes.wmf': make_shapes_wmf(),
        'shapes.emf': make_shapes_emf(),
        'plus.emf': make_plus_emf(),
        'plus-fallback.emf': make_plus_fallback_emf(),
        'plus-only.emf': make_plus_only_emf(),
        'glyphs.emf': make_glyphs_emf(),
        'arcs.wmf': make_arcs_wmf(),
        'modes.emf': make_modes_emf(),
        'embedded.wmf': embedded_wmf(make_shapes_emf()),
        'embedded-glyphs.wmf': embedded_wmf(make_glyphs_emf()),
        'nested-2.emf': make_nested_emf(2),
        'nested-6.emf': make_nested_emf(6),
    }
    for name, data in files.items():
        with open(os.path.join(HERE, name), 'wb') as f:
            f.write(data)
        print(name, len(data))
