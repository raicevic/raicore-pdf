"""Generates the XFA form fixtures (run with a venv that has pikepdf and pymupdf; from this folder: python make-xfa.py).

- xfa-static.pdf: pymupdf-form.pdf with a static XFA form added (the XFA packets as an array of streams).
- xfa-dynamic.pdf: the same with /NeedsRendering true, so a dynamic XFA form with a full AcroForm behind it.
- xfa-placeholder.pdf: a dynamic XFA form whose only PDF page is the "Please wait..." placeholder that XFA designers
  write, with an AcroForm that has no fields. The XFA is one stream here (the other form XFA can take)."""
import fitz
import pikepdf

TEMPLATE = b"""<template xmlns="http://www.xfa.org/schema/xfa-template/3.3/">
  <subform name="form1" layout="tb" locale="en_US">
    <pageSet><pageArea name="Page1"><contentArea x="0.25in" y="0.25in" w="8in" h="10.5in"/>
      <medium stock="letter" short="8.5in" long="11in"/></pageArea></pageSet>
    <field name="name" w="3in" h="9mm"><ui><textEdit/></ui><caption><value><text>Name</text></value></caption></field>
  </subform>
</template>"""

DATASETS = b"""<xfa:datasets xmlns:xfa="http://www.xfa.org/schema/xfa-data/1.0/">
  <xfa:data><form1><name>Ada Lovelace</name></form1></xfa:data>
</xfa:datasets>"""

PREAMBLE = b'<xdp:xdp xmlns:xdp="http://ns.adobe.com/xdp/">'
POSTAMBLE = b"</xdp:xdp>"


def xfa_array(pdf):
    parts = [("preamble", PREAMBLE), ("template", TEMPLATE), ("datasets", DATASETS), ("postamble", POSTAMBLE)]
    items = []
    for name, data in parts:
        items.append(pikepdf.String(name))
        items.append(pdf.make_stream(data))
    return pikepdf.Array(items)


def with_xfa(source, target, dynamic):
    pdf = pikepdf.open(source)
    pdf.Root.AcroForm.XFA = xfa_array(pdf)
    if dynamic:
        pdf.Root.NeedsRendering = True
    pdf.save(target, deterministic_id=True)


with_xfa("pymupdf-form.pdf", "xfa-static.pdf", dynamic=False)
with_xfa("pymupdf-form.pdf", "xfa-dynamic.pdf", dynamic=True)

doc = fitz.open()
page = doc.new_page(width=612, height=792)
page.insert_text((72, 100), "Please wait...", fontsize=18)
page.insert_text((72, 130), "If this message is not eventually replaced by the proper contents of the document,", fontsize=10)
page.insert_text((72, 144), "your PDF viewer may not be able to display this type of document.", fontsize=10)
doc.save("xfa-placeholder.tmp.pdf")
doc.close()

pdf = pikepdf.open("xfa-placeholder.tmp.pdf")
pdf.Root.AcroForm = pdf.make_indirect(pikepdf.Dictionary(
    Fields=pikepdf.Array(),
    XFA=pdf.make_stream(PREAMBLE + TEMPLATE + DATASETS + POSTAMBLE)))
pdf.Root.NeedsRendering = True
pdf.save("xfa-placeholder.pdf", deterministic_id=True)
pdf.close()

import os
os.remove("xfa-placeholder.tmp.pdf")
