"""Generates images.pdf: one page with an image of every kind the image extractor handles (a seed for the extract fuzz
target and a fixture). Run with a venv that has pikepdf and pillow, from this folder: python make-images-fixture.py"""
import io, zlib
import pikepdf
from PIL import Image

pdf = pikepdf.new()
page = pdf.add_blank_page(page_size=(300, 400))
xobjects = pikepdf.Dictionary()

def image(name, data, **entries):
    stream = pikepdf.Stream(pdf, data)
    stream.Type, stream.Subtype = pikepdf.Name.XObject, pikepdf.Name.Image
    for key, value in entries.items():
        stream[pikepdf.Name("/" + key)] = value
    xobjects[pikepdf.Name("/" + name)] = stream
    return stream

w, h = 8, 6
rgb = bytes(v for y in range(h) for x in range(w) for v in (x * 30, y * 40, 128))
alpha = image("Alpha", zlib.compress(bytes(x * 30 for y in range(h) for x in range(w))), Width=w, Height=h, ColorSpace=pikepdf.Name.DeviceGray, BitsPerComponent=8, Filter=pikepdf.Name.FlateDecode)
image("Rgb", zlib.compress(rgb), Width=w, Height=h, ColorSpace=pikepdf.Name.DeviceRGB, BitsPerComponent=8, Filter=pikepdf.Name.FlateDecode, SMask=alpha)
image("Indexed", bytes([0x01, 0x23, 0x45, 0x67]), Width=8, Height=1, BitsPerComponent=4, ColorSpace=pikepdf.Array([pikepdf.Name.Indexed, pikepdf.Name.DeviceRGB, 7, pikepdf.String(bytes(range(24)))]))
image("Gray1", bytes([0b10101010, 0b01010101]), Width=8, Height=2, ColorSpace=pikepdf.Name.DeviceGray, BitsPerComponent=1, Decode=pikepdf.Array([1, 0]))
image("Cmyk", bytes([0, 0, 0, 0, 255, 0, 0, 0, 0, 0, 0, 255]), Width=3, Height=1, ColorSpace=pikepdf.Name.DeviceCMYK, BitsPerComponent=8)
mask = image("Mask", bytes([0b01000000]), Width=2, Height=1, ImageMask=True)
image("Masked", bytes([10, 20]), Width=2, Height=1, ColorSpace=pikepdf.Name.DeviceGray, BitsPerComponent=8, Mask=mask)
image("Keyed", bytes([10, 20, 30]), Width=3, Height=1, ColorSpace=pikepdf.Name.DeviceGray, BitsPerComponent=8, Mask=pikepdf.Array([15, 25]))
jpeg = io.BytesIO(); Image.new("RGB", (8, 8), (200, 50, 50)).save(jpeg, "JPEG", quality=50)
image("Jpeg", jpeg.getvalue(), Width=8, Height=8, ColorSpace=pikepdf.Name.DeviceRGB, BitsPerComponent=8, Filter=pikepdf.Name.DCTDecode)
image("Fax", bytes.fromhex("23861173fc60020020"), Width=16, Height=4, ColorSpace=pikepdf.Name.DeviceGray, BitsPerComponent=1,
      Filter=pikepdf.Name.CCITTFaxDecode, DecodeParms=pikepdf.Dictionary(K=-1, Columns=16, Rows=4))
image("Jpx", b"\x00\x00\x00\x0cjP  \r\n\x87\n", Width=1, Height=1, Filter=pikepdf.Name.JPXDecode)

page.Resources = pikepdf.Dictionary(XObject=xobjects)
names = ["Rgb", "Indexed", "Gray1", "Cmyk", "Masked", "Keyed", "Jpeg", "Fax", "Jpx", "Mask"]
content = "".join(f"q 40 0 0 30 {10 + (i % 5) * 55} {300 - (i // 5) * 50} cm /{n} Do Q\n" for i, n in enumerate(names))
content += "q 0 40 -30 0 100 150 cm /Rgb Do Q\n"  # Turned.
content += "q 20 0 0 10 10 100 cm BI /W 2 /H 1 /CS /RGB /BPC 8 /F /AHx ID ff000000ff00> EI Q\n"  # Inline.
page.Contents = pdf.make_stream(content.encode())
pdf.save("images.pdf", compress_streams=False, object_stream_mode=pikepdf.ObjectStreamMode.disable)
