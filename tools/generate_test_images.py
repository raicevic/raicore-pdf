"""Generates the JPEG and GIF fixtures in tests/RaiCore.Pdf.Tests/Fixtures/Images (needs Pillow).

Every image shows four colored quadrants (red top-left, green top-right, blue bottom-left, yellow
bottom-right) when displayed correctly, so tests can check orientation and color by rendering.
PNG coverage comes from PngSuite (Fixtures/PngSuite).

Usage: python tools/generate_test_images.py
"""
from pathlib import Path
from PIL import Image, ImageOps

out = Path(__file__).resolve().parent.parent / "tests/RaiCore.Pdf.Tests/Fixtures/Images"
out.mkdir(parents=True, exist_ok=True)

W, H = 64, 48
QUADRANTS = [(255, 0, 0), (0, 200, 0), (0, 0, 255), (255, 220, 0)]


def canonical(mode="RGB"):
    img = Image.new("RGB", (W, H))
    for y in range(H):
        for x in range(W):
            img.putpixel((x, y), QUADRANTS[(2 if y >= H // 2 else 0) + (1 if x >= W // 2 else 0)])
    return img.convert(mode)


# Orientation N means "transform the stored pixels with Pillow's exif_transpose method to display them".
# Store the inverse so the displayed result is the canonical image.
INVERSE = {
    2: Image.Transpose.FLIP_LEFT_RIGHT, 3: Image.Transpose.ROTATE_180, 4: Image.Transpose.FLIP_TOP_BOTTOM,
    5: Image.Transpose.TRANSPOSE, 6: Image.Transpose.ROTATE_90, 7: Image.Transpose.TRANSVERSE,
    8: Image.Transpose.ROTATE_270,
}

canonical().save(out / "rgb.jpg", quality=95)
canonical().save(out / "rgb-progressive.jpg", quality=95, progressive=True)
canonical("L").save(out / "gray.jpg", quality=95)
canonical("CMYK").save(out / "cmyk.jpg", quality=95)  # Pillow writes Adobe (inverted) CMYK.
for orientation, transpose in INVERSE.items():
    stored = canonical().transpose(transpose)
    exif = Image.Exif()
    exif[0x0112] = orientation
    path = out / f"orientation-{orientation}.jpg"
    stored.save(path, quality=95, exif=exif)
    shown = ImageOps.exif_transpose(Image.open(path))
    assert shown.size == (W, H) and shown.getpixel((2, 2))[0] > 200 and shown.getpixel((W - 3, H - 3))[1] > 150, orientation

gif = canonical().quantize(colors=8)
gif.save(out / "quadrants.gif", interlace=False)
gif.save(out / "quadrants-interlaced.gif", interlace=True)
transparent = canonical().convert("RGBA")
for y in range(H // 2, H):
    for x in range(W // 2):
        transparent.putpixel((x, y), (0, 0, 0, 0))  # Bottom-left quadrant transparent.
transparent.save(out / "transparent.gif")
frames = [canonical(), Image.new("RGB", (W, H), (0, 0, 0))]
frames[0].save(out / "animated.gif", save_all=True, append_images=frames[1:], duration=100, loop=0)
print("fixtures written to", out)
