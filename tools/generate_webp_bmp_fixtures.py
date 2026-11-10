"""Generates the BMP and WebP fixtures in tests/RaiCore.Pdf.Tests/Fixtures/{Bmp,WebP} (needs Pillow).

Each fixture NAME.bmp / NAME.webp comes with NAME.png, the RGBA pixels it must decode to:

- BMP files are written byte by byte here (Pillow writes only a few variants), so every header version, bit
  depth, compression and mask layout is covered. The expected pixels are the source image as each variant
  stores it; where Pillow can read the variant, the script checks that Pillow agrees.
- WebP files are encoded with libwebp (Pillow's, and through ctypes the libwebp library bundled with Pillow
  for the encoder settings Pillow does not expose: simple loop filter, token partitions, raw alpha...). ALPH
  chunks with each filter method are assembled here. The expected pixels are libwebp's own decoding
  (through Pillow), so the decoder is checked bit for bit against the reference implementation.

Usage: python tools/generate_webp_bmp_fixtures.py
"""
import ctypes
import glob
import io
import math
import os
import random
import struct
from pathlib import Path

import PIL
from PIL import Image, ImageFilter

root = Path(__file__).resolve().parent.parent / "tests/RaiCore.Pdf.Tests/Fixtures"
bmp_dir, webp_dir = root / "Bmp", root / "WebP"
for d in (bmp_dir, webp_dir):
    d.mkdir(parents=True, exist_ok=True)
    for f in d.iterdir():
        f.unlink()


def source(width, height, seed=1, alpha=True):
    """A test picture: gradients, saturated shapes with sharp edges, noise, and an alpha ramp with a hole."""
    rnd = random.Random(seed)
    img = Image.new("RGBA", (width, height))
    px = img.load()
    for y in range(height):
        for x in range(width):
            r = int(255 * x / max(1, width - 1))
            g = int(255 * y / max(1, height - 1))
            b = int(128 + 127 * math.sin((x + 2 * y) / 5.0))
            if (x - width * 0.3) ** 2 + (y - height * 0.6) ** 2 < (min(width, height) * 0.25) ** 2:
                r, g, b = 250, 40, 30
            if width * 0.55 < x < width * 0.85 and height * 0.15 < y < height * 0.45:
                r, g, b = 20, 30, 200
            if x > width * 0.7 and y > height * 0.6:
                n = rnd.randrange(-60, 60)
                r, g, b = (max(0, min(255, c + n)) for c in (r, g, b))
            a = 255
            if alpha:
                a = max(0, min(255, int(255 * (1.2 - math.hypot(x - width / 2, y - height / 2) / (width / 2)))))
                if abs(x - width * 0.2) < 3 and abs(y - height * 0.2) < 3:
                    a = 0
            px[x, y] = (r, g, b, a)
    return img


def save_png(img, path):
    img.convert("RGBA").save(path, optimize=True)


# ---------------------------------------------------------------- BMP

def file_header(pixel_offset, total):
    return b"BM" + struct.pack("<IHHI", total, 0, 0, pixel_offset)


def info_header(size, width, height, bpp, compression, colors=0, masks=None):
    header = struct.pack("<iiHHIIiiII", width, height, 1, bpp, compression, 0, 2835, 2835, colors, 0)
    if size >= 52:
        header += struct.pack("<III", *(masks or (0, 0, 0))[:3])
    if size >= 56:
        header += struct.pack("<I", (masks or (0, 0, 0, 0))[3] if masks and len(masks) > 3 else 0)
    if size >= 108:
        header += b"sRGB" + bytes(36) + bytes(12)  # LCS_sRGB, endpoints and gamma unused
    if size >= 124:
        header += struct.pack("<IIII", 4, 0, 0, 0)  # LCS_GM_IMAGES, no profile
    assert len(header) == size - 4, (size, len(header))
    return struct.pack("<I", size) + header


def rows(height, top_down):
    return range(height) if top_down else range(height - 1, -1, -1)


def pad(row):
    return row + bytes(-len(row) % 4)


def write_bmp(name, header, extra, pixels, expected, pillow_check=True, tolerance=0):
    offset = 14 + len(header) + len(extra)
    data = file_header(offset, offset + len(pixels)) + header + extra + pixels
    (bmp_dir / f"{name}.bmp").write_bytes(data)
    save_png(expected, bmp_dir / f"{name}.png")
    if pillow_check:
        theirs = Image.open(io.BytesIO(data)).convert("RGBA")
        ours = expected.convert("RGBA")
        if theirs.mode == "RGBA" and expected.mode == "RGB":
            theirs = theirs.convert("RGB").convert("RGBA")
        # Pillow scales channels of fewer than 8 bits by truncating (v * 255 / max); we round, so allow 1.
        worst = max(abs(a - b) for p, q in zip(theirs.getdata(), ours.getdata()) for a, b in zip(p, q))
        assert worst <= tolerance, f"Pillow disagrees on {name} by {worst}"


def palette_image(img, colors):
    """Quantizes to a palette; returns (index rows, palette RGB list, the RGB image the indices show)."""
    q = img.convert("RGB").quantize(colors=colors, dither=Image.Dither.NONE)
    pal = q.getpalette()[: 3 * colors]
    pal += [0] * (3 * colors - len(pal))
    w, h = q.size
    idx = list(q.getdata())
    shown = Image.new("RGB", (w, h))
    shown.putdata([tuple(pal[3 * i: 3 * i + 3]) for i in idx])
    return [idx[y * w:(y + 1) * w] for y in range(h)], [tuple(pal[3 * i: 3 * i + 3]) for i in range(colors)], shown


def pack_indices(row, bpp):
    out, acc, n = bytearray(), 0, 0
    for i in row:
        acc = (acc << bpp) | i
        n += bpp
        if n == 8:
            out.append(acc)
            acc, n = 0, 0
    if n:
        out.append(acc << (8 - n))
    return bytes(out)


def bmp_palettized(name, width, height, bpp, colors, header_size=40, top_down=False, colors_used=0):
    idx, pal, shown = palette_image(source(width, height, seed=bpp, alpha=False), colors)
    if header_size == 12:
        header = struct.pack("<IHHHH", 12, width, height, 1, bpp)
        table = b"".join(bytes((b, g, r)) for r, g, b in pal)
    else:
        header = info_header(header_size, width, -height if top_down else height, bpp, 0, colors_used)
        table = b"".join(bytes((b, g, r, 0)) for r, g, b in pal)
    pixels = b"".join(pad(pack_indices(idx[y], bpp)) for y in rows(height, top_down))
    write_bmp(name, header, table, pixels, shown, pillow_check=bpp != 2)


def bmp_24(name, width, height, core=False):
    img = source(width, height, seed=24, alpha=False).convert("RGB")
    header = struct.pack("<IHHHH", 12, width, height, 1, 24) if core else info_header(40, width, height, 24, 0)
    px = img.load()
    pixels = b"".join(pad(b"".join(bytes((px[x, y][2], px[x, y][1], px[x, y][0])) for x in range(width)))
                      for y in rows(height, False))
    write_bmp(name, header, b"", pixels, img)


def scale(value, bits):
    if bits == 0:
        return 0
    if bits >= 8:
        return value >> (bits - 8)
    m = (1 << bits) - 1
    return (value * 255 + m // 2) // m


def bmp_fields(name, width, height, bpp, masks, header_size=40, compression=3, top_down=False,
               zero_alpha=False, pillow_check=True):
    """16/32-bit pixels with the given (r, g, b, a) masks; the expected image applies the same scaling."""
    img = source(width, height, seed=bpp + len(masks))
    px = img.load()
    expected = Image.new("RGBA", (width, height))
    epx = expected.load()
    has_alpha = len(masks) > 3 and masks[3] and not zero_alpha

    def field(mask, value8):
        shift = (mask & -mask).bit_length() - 1 if mask else 0
        bits = (mask >> shift).bit_length() if mask else 0
        v = value8 >> (8 - bits) if bits <= 8 else value8 << (bits - 8)
        return (v << shift) & mask, scale(v, bits)

    data_rows = []
    for y in rows(height, top_down):
        row = b""
        for x in range(width):
            r, g, b, a = px[x, y]
            word, out = 0, []
            for mask, value in zip(masks, (r, g, b, 0 if zero_alpha else a)):
                bitsv, scaled = field(mask, value)
                word |= bitsv
                out.append(scaled)
            alpha_out = out[3] if has_alpha else 255
            epx[x, y] = (out[0], out[1], out[2], alpha_out)
            row += struct.pack("<H" if bpp == 16 else "<I", word)
        data_rows.append(pad(row))
    if header_size == 40 and compression in (3, 6):
        header = info_header(40, width, -height if top_down else height, bpp, compression)
        extra = b"".join(struct.pack("<I", m) for m in masks[: 4 if compression == 6 else 3])
    else:
        header = info_header(header_size, width, -height if top_down else height, bpp, compression,
                             masks=tuple(masks) + (0,) * (4 - len(masks)))
        extra = b""
    write_bmp(name, header, extra, b"".join(data_rows), expected, pillow_check, tolerance=1 if bpp == 16 else 0)


def rle_encode(idx_rows, width, rle4):
    """Encodes with every escape: runs, absolute runs (odd lengths too), a delta that skips pixels, EOL, EOF."""
    out = bytearray()
    height = len(idx_rows)
    y = 0
    skipped = set()
    while y < height:
        row = idx_rows[height - 1 - y]  # Bottom-up.
        if y == 2:
            # Delta: skip 3 pixels right and 1 row up, leaving pixels transparent.
            out += bytes((0, 2, 3, 1))
            for x in range(width):
                skipped.add((x, height - 1 - y))
            for x in range(3):
                skipped.add((x, height - 1 - (y + 1)))
            y += 1
            row = idx_rows[height - 1 - y]
            x = 3
        else:
            x = 0
        while x < width:
            run = 1
            while x + run < width and run < 255 and row[x + run] == row[x]:
                run += 1
            if run >= 3 or width - x < 3:
                n = min(run, width - x)
                value = row[x] if not rle4 else (row[x] << 4) | row[x]
                out += bytes((n, value))
                x += n
            else:
                n = 3
                while x + n < width and n < 200 and not (x + n + 2 < width and row[x + n] == row[x + n + 1] == row[x + n + 2]):
                    n += 1
                n = min(n, width - x)
                literal = row[x:x + n]
                body = bytes(literal) if not rle4 else pack_indices(literal, 4)
                out += bytes((0, n)) + body + bytes(len(body) % 2)
                x += n
        y += 1
        if y == height - 1:
            # Leave the last (top) row out entirely: end of bitmap.
            for x in range(width):
                skipped.add((x, 0))
            break
        out += bytes((0, 0))
    out += bytes((0, 1))
    return bytes(out), skipped


def bmp_rle(name, width, height, rle4):
    colors = 16 if rle4 else 40
    img = source(width, height, seed=40 + rle4, alpha=False)
    # Posterize into flat bands so that runs occur, but keep detail on the left for absolute runs (odd and even).
    flat = img.resize((max(1, width // 4), max(1, height // 2)), Image.NEAREST).resize((width, height), Image.NEAREST)
    flat.paste(img.crop((0, 0, 7 + (width % 5), height)), (0, 0))
    img = flat
    idx, pal, shown = palette_image(img, colors)
    data, skipped = rle_encode(idx, width, rle4)
    header = info_header(40, width, height, 4 if rle4 else 8, 2 if rle4 else 1, colors)
    table = b"".join(bytes((b, g, r, 0)) for r, g, b in pal)
    expected = shown.convert("RGBA")
    epx = expected.load()
    for x, y in skipped:
        epx[x, y] = (0, 0, 0, 0)
    write_bmp(name, header, table, data, expected, pillow_check=False)


bmp_palettized("core-8bit", 29, 19, 8, 256, header_size=12)
bmp_24("core-24bit", 31, 17, core=True)
bmp_palettized("1bit", 37, 21, 1, 2)
bmp_palettized("2bit", 35, 13, 2, 4)
bmp_palettized("4bit", 33, 19, 4, 12, colors_used=12)
bmp_palettized("8bit", 31, 23, 8, 256)
bmp_palettized("8bit-topdown", 31, 23, 8, 100, top_down=True, colors_used=100)
bmp_palettized("8bit-v5", 30, 20, 8, 64, header_size=124, colors_used=64)
bmp_rle("rle8", 41, 23, rle4=False)
bmp_rle("rle4", 39, 21, rle4=True)
bmp_fields("16bit-555", 33, 19, 16, (0x7C00, 0x03E0, 0x001F), compression=0)
bmp_fields("16bit-565", 33, 19, 16, (0xF800, 0x07E0, 0x001F))
bmp_fields("16bit-4444-v3", 27, 15, 16, (0x0F00, 0x00F0, 0x000F, 0xF000), header_size=56, pillow_check=False)
bmp_24("24bit", 35, 21)
bmp_fields("32bit-rgb", 25, 17, 32, (0xFF0000, 0xFF00, 0xFF, 0xFF000000), compression=0, zero_alpha=True)
bmp_fields("32bit-rgb-alpha", 25, 17, 32, (0xFF0000, 0xFF00, 0xFF, 0xFF000000), compression=0, pillow_check=False)
bmp_fields("32bit-v4-alpha-topdown", 26, 18, 32, (0xFF0000, 0xFF00, 0xFF, 0xFF000000), header_size=108, top_down=True)
bmp_fields("32bit-v5-alpha", 26, 18, 32, (0xFF00, 0xFF0000, 0xFF000000, 0xFF), header_size=124, pillow_check=False)
bmp_fields("32bit-alphabitfields-2101010", 24, 16, 32, (0x3FF00000, 0xFFC00, 0x3FF, 0xC0000000), compression=6,
           pillow_check=False)

# ---------------------------------------------------------------- WebP


def find_libwebp():
    base = Path(PIL.__file__).parent
    for pattern in (".dylibs/libwebp.*.dylib", "../pillow.libs/libwebp-*.so*", "../Pillow.libs/libwebp-*.so*"):
        hits = [h for h in glob.glob(str(base / pattern)) if "demux" not in h and "mux" not in os.path.basename(h)]
        if hits:
            return ctypes.CDLL(hits[0])
    raise SystemExit("libwebp bundled with Pillow not found")


lib = find_libwebp()


class WebPConfig(ctypes.Structure):
    _fields_ = [(n, ctypes.c_float if n in ("quality", "target_PSNR") else ctypes.c_int) for n in (
        "lossless quality method image_hint target_size target_PSNR segments sns_strength filter_strength "
        "filter_sharpness filter_type autofilter alpha_compression alpha_filtering alpha_quality pass "
        "show_compressed preprocessing partitions partition_limit emulate_jpeg_size thread_level low_memory "
        "near_lossless exact use_delta_palette use_sharp_yuv qmin qmax").split()]


P = ctypes.c_void_p
I = ctypes.c_int
U = ctypes.c_uint32


class WebPPicture(ctypes.Structure):
    _fields_ = [("use_argb", I), ("colorspace", I), ("width", I), ("height", I), ("y", P), ("u", P), ("v", P),
                ("y_stride", I), ("uv_stride", I), ("a", P), ("a_stride", I), ("pad1", U * 2), ("argb", P),
                ("argb_stride", I), ("pad2", U * 3), ("writer", P), ("custom_ptr", P), ("extra_info_type", I),
                ("extra_info", P), ("stats", P), ("error_code", I), ("progress_hook", P), ("user_data", P),
                ("pad3", U * 3), ("pad4", P), ("pad5", P), ("pad6", U * 8), ("memory_", P), ("memory_argb_", P),
                ("pad7", P * 2)]


class WebPMemoryWriter(ctypes.Structure):
    _fields_ = [("mem", ctypes.POINTER(ctypes.c_uint8)), ("size", ctypes.c_size_t), ("max_size", ctypes.c_size_t),
                ("pad", U * 1)]


ABI = 0x020F


def encode(img, **settings):
    """Encodes RGBA with libwebp's full WebPConfig (quality given as a float, lossless 0/1, and any field)."""
    config = WebPConfig()
    assert lib.WebPConfigInitInternal(ctypes.byref(config), 0, ctypes.c_float(settings.pop("quality", 80)), ABI)
    for key, value in settings.items():
        setattr(config, key, value)
    assert lib.WebPValidateConfig(ctypes.byref(config)), settings
    pic = WebPPicture()
    assert lib.WebPPictureInitInternal(ctypes.byref(pic), ABI)
    pic.width, pic.height = img.size
    pic.use_argb = config.lossless
    rgba = img.convert("RGBA").tobytes()
    assert lib.WebPPictureImportRGBA(ctypes.byref(pic), rgba, 4 * img.size[0])
    writer = WebPMemoryWriter()
    lib.WebPMemoryWriterInit(ctypes.byref(writer))
    pic.writer = ctypes.cast(lib.WebPMemoryWrite, P).value
    pic.custom_ptr = ctypes.cast(ctypes.byref(writer), P).value
    assert lib.WebPEncode(ctypes.byref(config), ctypes.byref(pic)), pic.error_code
    data = ctypes.string_at(writer.mem, writer.size)
    lib.WebPMemoryWriterClear(ctypes.byref(writer))
    lib.WebPPictureFree(ctypes.byref(pic))
    return data


def chunks(data):
    """The chunks of a RIFF WebP file as (fourcc, payload)."""
    assert data[:4] == b"RIFF" and data[8:12] == b"WEBP"
    pos, out = 12, []
    while pos < len(data):
        tag, size = data[pos:pos + 4], struct.unpack("<I", data[pos + 4:pos + 8])[0]
        out.append((tag, data[pos + 8:pos + 8 + size]))
        pos += 8 + size + (size & 1)
    return out


def chunk(tag, payload):
    return tag + struct.pack("<I", len(payload)) + payload + bytes(len(payload) & 1)


def riff(*parts):
    body = b"WEBP" + b"".join(parts)
    return b"RIFF" + struct.pack("<I", len(body)) + body


def vp8x(width, height, alpha=False, animation=False):
    flags = (0x10 if alpha else 0) | (0x02 if animation else 0)
    return chunk(b"VP8X", struct.pack("<I", flags) + struct.pack("<I", width - 1)[:3] + struct.pack("<I", height - 1)[:3])


def write_webp(name, data):
    (webp_dir / f"{name}.webp").write_bytes(data)
    reference = Image.open(io.BytesIO(data))
    reference.seek(0)
    save_png(reference.convert("RGBA"), webp_dir / f"{name}.png")


def forward_filter(alpha, width, height, method):
    """The inverse of the decoder's unfiltering (libwebp's filters.c): returns the residuals."""
    out = bytearray(len(alpha))
    for y in range(height):
        for x in range(width):
            i = y * width + x
            if y == 0:
                pred = alpha[i - 1] if x > 0 else 0
            elif method == 1:
                pred = alpha[i - 1] if x > 0 else alpha[i - width]
            elif method == 2:
                pred = alpha[i - width]
            elif x == 0:
                pred = alpha[i - width]
            else:
                pred = max(0, min(255, alpha[i - 1] + alpha[i - width] - alpha[i - width - 1]))
            out[i] = (alpha[i] - pred) & 0xFF if method else alpha[i]
    return bytes(out)


def alph_chunk(alpha_img, method, filtering):
    width, height = alpha_img.size
    residuals = forward_filter(alpha_img.tobytes(), width, height, filtering)
    header = bytes((method | (filtering << 2),))
    if method == 0:
        return chunk(b"ALPH", header + residuals)
    # Lossless: a VP8L image whose green channel holds the values; drop the 5-byte VP8L header.
    green = Image.frombytes("L", (width, height), residuals)
    zero = Image.new("L", (width, height), 0)
    carrier = Image.merge("RGBA", (zero, green, zero, Image.new("L", (width, height), 255)))
    lossless = dict(chunks(encode(carrier, lossless=1, quality=75, exact=1)))[b"VP8L"]
    return chunk(b"ALPH", header + lossless[5:])


photo = source(75, 53, seed=7)
opaque = photo.convert("RGB")

write_webp("lossy", encode(opaque, quality=80))
write_webp("lossy-q20", encode(opaque, quality=20, sns_strength=0))
write_webp("lossy-q100-nofilter", encode(opaque, quality=100, filter_strength=0, autofilter=0))
write_webp("lossy-simple-filter", encode(opaque, quality=60, filter_type=0, filter_strength=70, autofilter=0))
# Several token partitions need method < 3 (libwebp's token buffer works with one) and more than 8 macroblock rows.
write_webp("lossy-sharp-partitions", encode(source(48, 140, seed=9).convert("RGB"), quality=50, filter_sharpness=6,
                                            filter_strength=60, partitions=3, segments=1, method=2))
write_webp("lossy-segments", encode(source(96, 80, seed=11).convert("RGB"), quality=70, segments=4,
                                    sns_strength=100, method=4))
flat = Image.new("RGB", (128, 128), (245, 245, 240))
flat.paste((200, 20, 20), (16, 16, 48, 48))
write_webp("lossy-flat", encode(flat, quality=50, method=2))  # Macroblocks without coefficients: the skip flag.
write_webp("lossy-large", encode(source(160, 120, seed=31).convert("RGB"), quality=75, method=4))
write_webp("lossy-1x1", encode(Image.new("RGB", (1, 1), (200, 30, 90)), quality=90))
write_webp("lossy-17x2", encode(source(17, 2, seed=3).convert("RGB"), quality=85))
write_webp("lossy-alpha", encode(photo, quality=80))  # libwebp's own ALPH (lossless, its filter choice)
write_webp("lossy-alpha-quantized", encode(photo, quality=75, alpha_quality=30, alpha_filtering=2))

small = source(37, 23, seed=5)
small_vp8 = dict(chunks(encode(small.convert("RGB"), quality=80)))[b"VP8 "]
for method, method_name in ((0, "raw"), (1, "lossless")):
    for filtering, filter_name in enumerate(("none", "horizontal", "vertical", "gradient")):
        write_webp(f"alpha-{method_name}-{filter_name}",
                   riff(vp8x(37, 23, alpha=True), alph_chunk(small.getchannel("A"), method, filtering),
                        chunk(b"VP8 ", small_vp8)))

write_webp("lossless", encode(opaque, lossless=1, quality=75))
write_webp("lossless-alpha", encode(photo, lossless=1, quality=75, exact=1))
write_webp("lossless-best", encode(source(128, 96, seed=13), lossless=1, quality=100, method=6, exact=1))
write_webp("lossless-fast", encode(source(64, 40, seed=17), lossless=1, quality=0, method=0, exact=1))
noise_rnd = random.Random(5)
noise = Image.new("RGB", (96, 96))
noise.putdata([(noise_rnd.randrange(256), noise_rnd.randrange(256), noise_rnd.randrange(256)) for _ in range(96 * 96)])
write_webp("lossless-blurred-noise", encode(noise.filter(ImageFilter.GaussianBlur(2)), lossless=1, quality=100, method=4))
for colors in (2, 4, 11, 200):
    img = source(45, 27, seed=colors).convert("RGB").quantize(colors=colors, dither=Image.Dither.NONE).convert("RGBA")
    write_webp(f"lossless-palette-{colors}", encode(img, lossless=1, quality=75))

# An animation whose first frame covers only part of the canvas, at an offset.
frame0 = source(30, 20, seed=21)
frame1 = source(40, 30, seed=22)
f0 = [c for c in chunks(encode(frame0, quality=80)) if c[0] in (b"ALPH", b"VP8 ")]
f1 = [c for c in chunks(encode(frame1, lossless=1)) if c[0] == b"VP8L"]


def anmf(x, y, w, h, frame_chunks):
    head = b"".join(struct.pack("<I", v)[:3] for v in (x // 2, y // 2, w - 1, h - 1, 100)) + bytes((0,))
    return chunk(b"ANMF", head + b"".join(chunk(t, p) for t, p in frame_chunks))


write_webp("animated", riff(vp8x(40, 30, alpha=True, animation=True), chunk(b"ANIM", bytes(4) + struct.pack("<H", 0)),
                            anmf(6, 4, 30, 20, f0), anmf(0, 0, 40, 30, f1)))

print("fixtures written to", bmp_dir, "and", webp_dir)
