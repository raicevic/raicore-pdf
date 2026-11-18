"""Generates the TIFF fixtures in tests/RaiCore.Pdf.Tests/Fixtures/Images/Tiff (needs Pillow, numpy, tifffile and
imagecodecs).

Each fixture NAME.tif comes with NAME.png, the RGBA pixels its first page must display as (pages after the first:
NAME.page2.png, NAME.page3.png, ...). The files come from three independent writers:

- Pillow (through libtiff) writes the CCITT fax and JPEG files, and the expected pixels are Pillow's (libtiff's
  and libjpeg's) own decoding, so the fax photometric and JPEG color handling are checked against the reference
  implementation.
- tifffile writes common layouts: strips, tiles, separate planes, predictors, both byte orders, BigTIFF and extra
  samples.
- The rest (2- and 4-bit samples, FillOrder 2, 8-bit color maps) is written byte by byte here.

For decoded (not JPEG) files the expected pixels are computed here from the source picture; where Pillow reads a
file too, the script checks that Pillow agrees and reports any difference.

Files named unsupported-*.tif are valid TIFFs that RaiCore.Pdf rejects with a clear message; they have no PNG.

Usage: python tools/generate_tiff_fixtures.py
"""
import io
import math
import random
import struct
import zlib
from pathlib import Path

import imagecodecs
import numpy as np
import tifffile
from PIL import Image

out = Path(__file__).resolve().parent.parent / "tests/RaiCore.Pdf.Tests/Fixtures/Images/Tiff"
out.mkdir(parents=True, exist_ok=True)
for f in out.iterdir():
    f.unlink()

W, H = 40, 28


def picture(width=W, height=H, seed=1):
    """RGBA test picture: gradients, saturated shapes with sharp edges, noise, and an alpha ramp with a hole."""
    rnd = random.Random(seed)
    a = np.zeros((height, width, 4), np.uint8)
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
            alpha = max(0, min(255, int(255 * (1.2 - math.hypot(x - width / 2, y - height / 2) / (width / 2)))))
            if abs(x - width * 0.2) < 3 and abs(y - height * 0.2) < 3:
                alpha = 0
            a[y, x] = (r, g, b, alpha)
    return a


def bilevel(width, height):
    """A fax-like 1-bit picture (True = black): text-like bars, a ring, a diagonal and a checkered corner."""
    a = np.zeros((height, width), bool)
    for y in range(height):
        for x in range(width):
            ring = abs(math.hypot(x - width * 0.7, y - height * 0.5) - min(width, height) * 0.3) < 2.5
            bars = 4 <= x < width * 0.45 and (y // 4) % 2 == 0 and (x * 7 + y * 3) % 23 > 4
            diagonal = abs(x - y * width / height) < 1.5
            checker = x > width * 0.85 and y > height * 0.8 and (x + y) % 2 == 0
            a[y, x] = ring or bars or diagonal or checker
    return a


SRC = picture()
GRAY = (SRC[..., :3].astype(np.float64) @ [0.299, 0.587, 0.114]).round().astype(np.uint8)
RGB16 = (SRC[..., :3].astype(np.uint16) * 257) ^ np.uint16(0x5A)  # Low bytes that differ from the high ones.
GRAY16 = (GRAY.astype(np.uint16) * 257) ^ np.uint16(0x33)
BITS = bilevel(W, H)


def rgba(rgb, alpha=None):
    """An RGBA array from gray (2-D) or RGB samples (8-bit) and optional alpha."""
    if rgb.ndim == 2:
        rgb = np.stack([rgb] * 3, axis=-1)
    a = np.full(rgb.shape[:2] + (1,), 255, np.uint8) if alpha is None else alpha[..., None]
    return np.concatenate([rgb, a], axis=-1).astype(np.uint8)


def save_png(pixels, name):
    Image.fromarray(np.ascontiguousarray(pixels, np.uint8)).save(out / name, optimize=True)


def check_with_pillow(name, expected, page=0):
    """Reports where Pillow decodes a file differently from the expected pixels (ignoring color under alpha 0)."""
    try:
        im = Image.open(out / name)
        im.seek(page)
        got = np.asarray(im.convert("RGBA"))
    except Exception as e:  # noqa: BLE001 - Pillow does not read every variant
        print(f"  {name} p{page}: Pillow cannot read it ({type(e).__name__}: {e})")
        return
    if got.shape != expected.shape:
        print(f"  {name} p{page}: Pillow size {got.shape} != {expected.shape}")
        return
    diff = np.abs(got.astype(int) - expected.astype(int))
    diff[..., :3] *= (expected[..., 3:] > 0)
    if diff.max() > 1:
        print(f"  {name} p{page}: Pillow differs by up to {diff.max()} in {np.count_nonzero(diff.max(axis=-1) > 1)} pixels")


def fixture(name, expected, check=True):
    if expected is not None:
        save_png(expected, name.replace(".tif", ".png"))
        if check:
            check_with_pillow(name, expected)
    print(f"{name}: {(out / name).stat().st_size} bytes")


# ---------------------------------------------------------------- a byte-by-byte writer

SHORT, LONG, RATIONAL, UNDEFINED, LONG8 = 3, 4, 5, 7, 16
SIZES = {1: 1, 2: 1, SHORT: 2, LONG: 4, RATIONAL: 8, UNDEFINED: 1, LONG8: 8}


def write_tiff(name, pages, order="<", big=False):
    """Writes pages, each (tags, strips): tags {tag: (type, [values])} without the strip tags, strips a list of bytes.
    StripOffsets/StripByteCounts (or TileOffsets/TileByteCounts when tag 322 is present) are added."""
    e = order
    head = (b"II" if e == "<" else b"MM") + (struct.pack(e + "HHHQ", 43, 8, 0, 0) if big else struct.pack(e + "HI", 42, 0))
    data = bytearray(head)
    ifd_link = 8 if big else 4  # Where the first IFD offset goes.
    for tags, strips in pages:
        offsets = []
        for s in strips:
            if len(data) % 2:
                data.append(0)
            offsets.append(len(data))
            data += s
        tiled = 322 in tags
        tags = dict(tags)
        off_type = LONG8 if big else LONG
        tags[324 if tiled else 273] = (off_type, offsets)
        tags[325 if tiled else 279] = (off_type, [len(s) for s in strips])
        if len(data) % 2:
            data.append(0)
        ifd = len(data)
        struct.pack_into(e + ("Q" if big else "I"), data, ifd_link, ifd)
        entries = sorted(tags.items())
        entry_size, inline = (20, 8) if big else (12, 4)
        count_fmt = "Q" if big else "H"
        ifd_size = struct.calcsize(e + count_fmt) + entry_size * len(entries) + (8 if big else 4)
        extra = bytearray()
        body = bytearray(struct.pack(e + count_fmt, len(entries)))
        for tag, (typ, values) in entries:
            if typ == RATIONAL:
                raw = b"".join(struct.pack(e + "II", *v) for v in values)
            elif typ in (1, 2, UNDEFINED):
                raw = bytes(values)
            else:
                fmt = {SHORT: "H", LONG: "I", LONG8: "Q"}[typ]
                raw = b"".join(struct.pack(e + fmt, v) for v in values)
            count = len(values)
            if len(raw) <= inline:
                value = raw.ljust(inline, b"\0")
            else:
                pos = ifd + ifd_size + len(extra)
                extra += raw + (b"\0" if len(raw) % 2 else b"")
                value = struct.pack(e + ("Q" if big else "I"), pos)
            body += struct.pack(e + "HH" + ("Q" if big else "I"), tag, typ, count) + value
        ifd_link = ifd + len(body)
        body += b"\0" * (8 if big else 4)
        data += body + extra
    (out / name).write_bytes(bytes(data))


def base_tags(width, height, bits, samples, photometric, compression, rows=None):
    tags = {
        256: (LONG, [width]),
        257: (LONG, [height]),
        258: (SHORT, [bits] * samples),
        259: (SHORT, [compression]),
        262: (SHORT, [photometric]),
        277: (SHORT, [samples]),
        278: (LONG, [rows or height]),
    }
    return tags


def pack(samples, bits):
    """Packs rows of samples (2-D: rows x samples per row) MSB first, each row padded to a byte."""
    rows = []
    for row in samples:
        acc, n, out_row = 0, 0, bytearray()
        for v in row:
            acc = (acc << bits) | int(v)
            n += bits
            if n == 8:
                out_row.append(acc)
                acc, n = 0, 0
        if n:
            out_row.append(acc << (8 - n))
        rows.append(bytes(out_row))
    return rows


def strips_of(rows, per_strip, compress=lambda b: b):
    return [compress(b"".join(rows[i:i + per_strip])) for i in range(0, len(rows), per_strip)]


REVERSE = bytes(int(f"{b:08b}"[::-1], 2) for b in range(256))

# ---------------------------------------------------------------- bilevel and few-bit gray (written here)

rows = pack(BITS.astype(np.uint8), 1)
write_tiff("bilevel-none-mm.tif", [(base_tags(W, H, 1, 1, 1, 1, rows=10), strips_of(rows, 10))], order=">")
fixture("bilevel-none-mm.tif", rgba(np.where(BITS, 255, 0).astype(np.uint8)))

write_tiff("bilevel-packbits-miniswhite.tif", [(base_tags(W, H, 1, 1, 0, 32773, rows=7), strips_of(rows, 7, imagecodecs.packbits_encode))])
fixture("bilevel-packbits-miniswhite.tif", rgba(np.where(BITS, 0, 255).astype(np.uint8)))

tags = base_tags(W, H, 1, 1, 0, 1)
tags[266] = (SHORT, [2])  # FillOrder: least significant bit first.
write_tiff("bilevel-fillorder2.tif", [(tags, [bytes(REVERSE[b] for b in b"".join(rows))])])
fixture("bilevel-fillorder2.tif", rgba(np.where(BITS, 0, 255).astype(np.uint8)))

g2 = GRAY >> 6
write_tiff("gray2-lzw.tif", [(base_tags(W, H, 2, 1, 1, 5, rows=9), strips_of(pack(g2, 2), 9, imagecodecs.lzw_encode))])
fixture("gray2-lzw.tif", rgba((g2 * 85).astype(np.uint8)))

g4 = GRAY >> 4
write_tiff("gray4-packbits-miniswhite.tif", [(base_tags(W, H, 4, 1, 0, 32773), strips_of(pack(15 - g4, 4), H, imagecodecs.packbits_encode))])
fixture("gray4-packbits-miniswhite.tif", rgba((g4 * 17).astype(np.uint8)))

# Palette images: 4 bits (PackBits) and 2 bits with an 8-bit color map, as some writers store it.
pal16 = [(255, 255, 255), (0, 0, 0), (250, 40, 30), (20, 30, 200), (0, 160, 60), (255, 220, 0), (128, 128, 128), (90, 50, 20),
         (200, 120, 255), (0, 200, 200), (255, 128, 0), (60, 60, 60), (180, 180, 180), (120, 0, 60), (0, 80, 160), (240, 200, 160)]
rgb = SRC[..., :3].astype(int)
index = np.argmin(((rgb[:, :, None, :] - np.array(pal16)[None, None]) ** 2).sum(-1), axis=-1).astype(np.uint8)
colormap = [c[i] * 257 for i in range(3) for c in pal16]
tags = base_tags(W, H, 4, 1, 3, 32773, rows=12)
tags[320] = (SHORT, colormap)
write_tiff("palette4-packbits.tif", [(tags, strips_of(pack(index, 4), 12, imagecodecs.packbits_encode))])
fixture("palette4-packbits.tif", rgba(np.array(pal16, np.uint8)[index]))

index2 = index % 4
tags = base_tags(W, H, 2, 1, 3, 1)
tags[320] = (SHORT, [c[i] for i in range(3) for c in pal16[:4]])  # 0-255 values.
write_tiff("palette2-8bit-colormap.tif", [(tags, strips_of(pack(index2, 2), H))])
fixture("palette2-8bit-colormap.tif", rgba(np.array(pal16[:4], np.uint8)[index2]), check=False)  # Pillow reads the map as 16-bit.

# ---------------------------------------------------------------- tifffile layouts


def tif(name, data, expected, **kw):
    kw.setdefault("metadata", None)
    if kw.get("planarconfig") == "separate":
        data = np.ascontiguousarray(np.moveaxis(data, -1, 0))  # tifffile wants the planes first.
    tifffile.imwrite(out / name, data, **kw)
    fixture(name, expected)


tif("gray8-deflate-predictor-mm.tif", GRAY, rgba(GRAY), byteorder=">", compression="zlib", predictor=True, rowsperstrip=8)
tif("gray8-deflate32946.tif", GRAY, rgba(GRAY), compression="deflate")
tif("gray16-lzw-predictor.tif", GRAY16, rgba((GRAY16 >> 8).astype(np.uint8)), compression="lzw", predictor=True, rowsperstrip=5)
tif("gray16-none-mm.tif", GRAY16, rgba((GRAY16 >> 8).astype(np.uint8)), byteorder=">")
tif("rgb8-none-mm.tif", SRC[..., :3], rgba(SRC[..., :3]), byteorder=">", rowsperstrip=6)
tif("rgb8-lzw-predictor.tif", SRC[..., :3], rgba(SRC[..., :3]), compression="lzw", predictor=True)
tif("rgb8-packbits-planar.tif", SRC[..., :3], rgba(SRC[..., :3]), compression="packbits", planarconfig="separate", rowsperstrip=10)
tif("rgb8-deflate-tiled.tif", SRC[..., :3], rgba(SRC[..., :3]), compression="zlib", tile=(16, 16))
tif("rgb8-lzw-tiled-planar-mm.tif", SRC[..., :3], rgba(SRC[..., :3]), byteorder=">", compression="lzw", tile=(16, 16), planarconfig="separate")
tif("rgb16-deflate-predictor-planar.tif", RGB16, rgba((RGB16 >> 8).astype(np.uint8)), compression="zlib", predictor=True, planarconfig="separate")
tif("rgb16-lzw-predictor-tiled-mm.tif", RGB16, rgba((RGB16 >> 8).astype(np.uint8)), byteorder=">", compression="lzw", predictor=True, tile=(16, 32))
tif("rgba8-unassociated-lzw.tif", SRC, SRC, compression="lzw", extrasamples=["unassalpha"])
tif("rgba8-unassociated-tiled-planar.tif", SRC, SRC, compression="zlib", tile=(16, 16), planarconfig="separate", extrasamples=["unassalpha"])

# Associated alpha: the color is stored premultiplied, and divided out again (rounded) when read.
a = SRC[..., 3:].astype(int)
premultiplied = np.concatenate([(SRC[..., :3].astype(int) * a + 127) // 255, a], axis=-1).astype(np.uint8)
unpremultiplied = np.where(a > 0, np.minimum(255, (premultiplied[..., :3].astype(int) * 255 + a // 2) // np.maximum(a, 1)), 0)
tif("rgba8-associated-deflate.tif", premultiplied, rgba(unpremultiplied.astype(np.uint8), SRC[..., 3]),
    compression="zlib", extrasamples=["assocalpha"])

alpha16 = SRC[..., 3].astype(np.uint16) * 257
tif("rgba16-unassociated-mm.tif", np.concatenate([RGB16, alpha16[..., None]], axis=-1),
    rgba((RGB16 >> 8).astype(np.uint8), SRC[..., 3]), byteorder=">", extrasamples=["unassalpha"])
tif("graya8-unassociated.tif", np.stack([GRAY, SRC[..., 3]], axis=-1), rgba(GRAY, SRC[..., 3]),
    photometric="minisblack", planarconfig="contig", extrasamples=["unassalpha"], compression="packbits")
tif("rgb8-extra-unspecified.tif", SRC, rgba(SRC[..., :3]), extrasamples=["unspecified"], compression="lzw")

CMYK = np.stack([255 - SRC[..., 0], 255 - SRC[..., 1], 255 - SRC[..., 2], (GRAY // 4)], axis=-1).astype(np.uint8)


def cmyk_to_rgb(c):
    """The naive conversion the tests use to display CMYK samples."""
    c = c.astype(int)
    return ((255 - c[..., :3]) * (255 - c[..., 3:]) // 255).astype(np.uint8)


tif("cmyk8-lzw.tif", CMYK, rgba(cmyk_to_rgb(CMYK)), photometric="separated", compression="lzw", predictor=True)
CMYK16 = CMYK.astype(np.uint16) * 257
tif("cmyk16-none-mm.tif", CMYK16, rgba(cmyk_to_rgb(CMYK)), byteorder=">", photometric="separated")

# Orientation 6: stored rotated 90 degrees counterclockwise, displayed upright (the picture).
tif("rgb8-orientation6.tif", np.ascontiguousarray(np.rot90(SRC[..., :3], 1)), rgba(SRC[..., :3]),
    compression="lzw", extratags=[(274, "H", 1, 6, True)])
tif("rgb8-bigtiff-lzw.tif", SRC[..., :3], rgba(SRC[..., :3]), bigtiff=True, compression="lzw", rowsperstrip=10)
tif("rgb8-bigtiff-tiled-mm.tif", SRC[..., :3], rgba(SRC[..., :3]), bigtiff=True, byteorder=">", compression="zlib", tile=(32, 16))

# Resolution: 150 x 75 dpi (a 2:1 pixel aspect, as fax files have).
tif("gray8-150x75dpi.tif", GRAY, rgba(GRAY), resolution=(150, 75), resolutionunit="INCH")
tif("gray8-59x59dpcm.tif", GRAY, rgba(GRAY), resolution=(59, 59), resolutionunit="CENTIMETER")

# A multi-page file: RGB with LZW, an 8-bit palette page, 16-bit gray tiles.
colormap256 = np.zeros((3, 256), np.uint16)
colormap256[:, :16] = np.array(colormap, np.uint16).reshape(3, 16)
with tifffile.TiffWriter(out / "multipage.tif") as w:
    w.write(SRC[..., :3], compression="lzw", metadata=None)
    w.write(index, photometric="palette", colormap=colormap256, metadata=None)
    w.write(GRAY16, tile=(16, 16), compression="zlib", metadata=None)
pages = [rgba(SRC[..., :3]), rgba(np.array(pal16, np.uint8)[index]), rgba((GRAY16 >> 8).astype(np.uint8))]
for p, expected in enumerate(pages):
    save_png(expected, "multipage.png" if p == 0 else f"multipage.page{p + 1}.png")
    check_with_pillow("multipage.tif", expected, p)
print(f"multipage.tif: {(out / 'multipage.tif').stat().st_size} bytes")

# ---------------------------------------------------------------- Pillow: JPEG and CCITT fax


def pil(name, image, page_images=None, **kw):
    """Saves with Pillow; the expected pixels are Pillow's decoding of the file it wrote."""
    if page_images:
        image.save(out / name, "TIFF", save_all=True, append_images=page_images, **kw)
    else:
        image.save(out / name, "TIFF", **kw)
    im = Image.open(out / name)
    for p in range(getattr(im, "n_frames", 1)):
        im.seek(p)
        save_png(np.asarray(im.convert("RGBA")), name.replace(".tif", ".png" if p == 0 else f".page{p + 1}.png"))
    print(f"{name}: {(out / name).stat().st_size} bytes")


photo = Image.fromarray(np.ascontiguousarray(picture(48, 40, seed=2)[..., :3]))
pil("rgb8-jpeg.tif", photo, compression="jpeg", quality=90)  # Photometric RGB, JPEGTables, one strip.
pil("rgb8-jpeg-strips.tif", photo, compression="jpeg", quality=90, tiffinfo={278: 16})  # Three strips, merged.
pil("gray8-jpeg-strips.tif", photo.convert("L"), compression="jpeg", quality=90, tiffinfo={278: 8})
pil("cmyk8-jpeg.tif", photo.convert("CMYK"), compression="jpeg", quality=90)
# tifffile: YCbCr JPEG with 2x2 subsampling, each strip a complete JPEG (16-row block rows).
# (photometric="ycbcr" would mean that the data given is YCbCr already.)
tifffile.imwrite(out / "ycbcr-jpeg-strips.tif", np.asarray(photo), compression=("jpeg", 90), rowsperstrip=16, metadata=None)
im = Image.open(out / "ycbcr-jpeg-strips.tif")
save_png(np.asarray(im.convert("RGBA")), "ycbcr-jpeg-strips.png")
print(f"ycbcr-jpeg-strips.tif: {(out / 'ycbcr-jpeg-strips.tif').stat().st_size} bytes")

FAX_W, FAX_H = 120, 64
fax = Image.fromarray(~bilevel(FAX_W, FAX_H)).convert("1")  # White background, black drawing.
pil("fax-g4.tif", fax, compression="group4", dpi=(204, 196))
pil("fax-g4-fillorder2.tif", fax, compression="group4", tiffinfo={266: 2})
pil("fax-g3-1d.tif", fax, compression="group3")
pil("fax-g3-2d-aligned.tif", fax, compression="group3", tiffinfo={292: 5})  # 2-D coding, EOLs byte-aligned.
pil("fax-mh.tif", fax, compression="tiff_ccitt")
pil("fax-mh-strips.tif", fax, compression="tiff_ccitt", tiffinfo={278: 16})
pil("fax-g4-multipage.tif", fax, page_images=[fax.transpose(Image.Transpose.ROTATE_180), fax.transpose(Image.Transpose.FLIP_LEFT_RIGHT)],
    compression="group4", dpi=(204, 98))

# The same G4 data marked WhiteIsZero (photometric 0) instead of Pillow's BlackIsZero: displayed inverted.
data = bytearray((out / "fax-g4.tif").read_bytes())
t = tifffile.TiffFile(out / "fax-g4.tif")
tag = t.pages[0].tags["PhotometricInterpretation"]
t.close()
struct.pack_into("<H", data, tag.valueoffset, 0)
(out / "fax-g4-miniswhite.tif").write_bytes(bytes(data))
expected = np.asarray(Image.open(out / "fax-g4-miniswhite.tif").convert("RGBA"))
save_png(expected, "fax-g4-miniswhite.png")
print(f"fax-g4-miniswhite.tif: {len(data)} bytes")

# ---------------------------------------------------------------- valid files RaiCore.Pdf rejects

fax.save(out / "unsupported-fax-g4-strips.tif", "TIFF", compression="group4", tiffinfo={278: 16})
tifffile.imwrite(out / "unsupported-float32.tif", GRAY.astype(np.float32) / 255, metadata=None)
# 8-row strips of YCbCr JPEG data subsampled 2 x 2 (16-row block rows), each a complete JPEG.
tags = base_tags(48, 24, 8, 3, 6, 7, rows=8)
tags[530] = (SHORT, [2, 2])  # YCbCrSubSampling
strips = [imagecodecs.jpeg8_encode(np.ascontiguousarray(np.asarray(photo)[y:y + 8]), level=90, subsampling=(2, 2)) for y in range(0, 24, 8)]
write_tiff("unsupported-ycbcr-jpeg-strips-of-8-rows.tif", [(tags, strips)])
for name in ("unsupported-fax-g4-strips.tif", "unsupported-float32.tif", "unsupported-ycbcr-jpeg-strips-of-8-rows.tif"):
    print(f"{name}: {(out / name).stat().st_size} bytes")
