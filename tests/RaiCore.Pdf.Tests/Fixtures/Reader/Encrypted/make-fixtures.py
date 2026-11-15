"""Writes the encrypted test PDFs in this folder with qpdf (through pikepdf), a writer independent of RaiCore.Pdf.

Run with: python3 -m venv venv && venv/bin/pip install pikepdf && venv/bin/python make-fixtures.py
Every file has the same two pages, a Unicode title and an XMP packet; only the encryption differs.
"""
import pikepdf
from pikepdf import Dictionary, Name, Array, Encryption, Permissions

def source():
    pdf = pikepdf.new()
    font = pdf.make_indirect(Dictionary(Type=Name.Font, Subtype=Name.Type1, BaseFont=Name.Helvetica))
    for text, color in ((b"One", b"1 0 0"), (b"Two", b"0 0 1")):
        content = color + b" rg 100 600 200 100 re f BT /F1 36 Tf 100 500 Td (" + text + b") Tj ET"
        page = pikepdf.Page(Dictionary(Type=Name.Page, MediaBox=Array([0, 0, 612, 792]),
                                       Resources=Dictionary(Font=Dictionary(F1=font)),
                                       Contents=pdf.make_stream(content)))
        pdf.pages.append(page)
    pdf.docinfo["/Title"] = "Årsrapport – fortrolig"
    with pdf.open_metadata(set_pikepdf_as_editor=False) as meta:
        meta["dc:title"] = "Årsrapport"
    return pdf

restricted = Permissions(print_lowres=True, print_highres=False, extract=False, modify_other=False,
                         modify_annotation=False, modify_form=False, modify_assembly=False, accessibility=True)
variants = {
    "rc4-40.pdf": (dict(R=2, aes=False, user="user", owner="owner", metadata=False), False),  # Revisions 2 and 3 have no /EncryptMetadata.
    "rc4-128.pdf": (dict(R=3, aes=False, user="", owner="owner", allow=restricted, metadata=False), False),
    "rc4-128-r4.pdf": (dict(R=4, aes=False, user="user", owner="owner", metadata=False), False),
    "aes-128-objstm.pdf": (dict(R=4, aes=True, user="user", owner="owner"), True),
    "aes-256-objstm.pdf": (dict(R=6, user="user", owner="owner", metadata=False), True),
}
for name, (options, objstm) in variants.items():
    pdf = source()
    mode = pikepdf.ObjectStreamMode.generate if objstm else pikepdf.ObjectStreamMode.disable
    pdf.save(name, encryption=Encryption(**options), object_stream_mode=mode)
    print(name)
