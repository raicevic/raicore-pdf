#!/usr/bin/env python3
"""Builds the XFA fixtures and their expectations (expected.json), independently of the C# engine.

Usage:
    python3 make-xfa.py                 # the hand-made forms (invoice.pdf, layouts.pdf) and expected.json
    python3 make-xfa.py --corpus DIR    # also the public government forms, from their PDFs downloaded into DIR

Needs pikepdf. The hand-made forms are minimal dynamic XFA PDFs (a "Please wait..." placeholder page, the XFA
packets as an array, /NeedsRendering true). The government forms (see README.md for their sources) are rebuilt the
same way from their own template, config and locale set packets, with generated data merged in, so they stay small.

The expectations come from this script's own reading of the XFA XML: which data values are shown, on which page and
in what order. For the hand-made forms the pages are computed from the template's fixed heights (a small paginator of
top-to-bottom flows, tables with overflow leaders, repeated subforms, content areas and page area breaks); for the
government forms, from the template's page-sized subforms.
"""

import argparse
import base64
import json
import os
import struct
import sys
import xml.etree.ElementTree as ET
import zlib

import pikepdf

HERE = os.path.dirname(os.path.abspath(__file__))
TEMPLATE_NS = "http://www.xfa.org/schema/xfa-template/3.3/"
DATA_NS = "http://www.xfa.org/schema/xfa-data/1.0/"

# The government forms, by file name (downloaded from https://www.gsa.gov/forms-library).
CORPUS = {
    "GSA52-85.pdf": "gsa52.pdf",
    "GSA55-02.pdf": "gsa55.pdf",
    "GSA7502-20.pdf": "gsa7502.pdf",
    "SF328-26a.pdf": "sf328.pdf",
    "GSA_2131.pdf": "gsa2131.pdf",
}


# --- Minimal PDFs ------------------------------------------------------------------------------------------------


def png(width, height):
    """A small two-color PNG (no image library needed)."""
    rows = b""
    for y in range(height):
        row = b"\x00"
        for x in range(width):
            row += bytes((200, 30, 30)) if x < width // 2 else bytes((30, 30, 200))
        rows += row

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b"")


def write_pdf(path, packets, title):
    """A dynamic XFA PDF: one placeholder page, the packets as an /XFA array, /NeedsRendering true."""
    pdf = pikepdf.new()
    font = pdf.make_indirect(pikepdf.Dictionary(Type=pikepdf.Name.Font, Subtype=pikepdf.Name.Type1, BaseFont=pikepdf.Name.Helvetica))
    content = b"BT /F1 12 Tf 72 720 Td (Please wait... If this message is not eventually replaced by the proper contents of the document, your PDF viewer may not be able to display this type of document.) Tj ET"
    page = pikepdf.Dictionary(
        Type=pikepdf.Name.Page,
        MediaBox=[0, 0, 612, 792],
        Resources=pikepdf.Dictionary(Font=pikepdf.Dictionary(F1=font)),
        Contents=pdf.make_stream(content),
    )
    pdf.pages.append(pikepdf.Page(page))
    xfa = pikepdf.Array()
    for name, data in packets:
        xfa.append(pikepdf.String(name))
        xfa.append(pdf.make_stream(data))
    pdf.Root.AcroForm = pdf.make_indirect(pikepdf.Dictionary(Fields=pikepdf.Array(), XFA=xfa))
    pdf.Root.NeedsRendering = True
    pdf.docinfo["/Title"] = title
    pdf.save(path, compress_streams=True, deterministic_id=True, object_stream_mode=pikepdf.ObjectStreamMode.generate)


def xdp_packets(template, datasets, config=None, locale_set=None):
    packets = [("preamble", b'<xdp:xdp xmlns:xdp="http://ns.adobe.com/xdp/">')]
    if config is not None:
        packets.append(("config", config))
    packets.append(("template", template))
    if locale_set is not None:
        packets.append(("localeSet", locale_set))
    if datasets is not None:
        packets.append(("datasets", datasets))
    packets.append(("postamble", b"</xdp:xdp>"))
    return packets


def datasets(record_xml):
    return (f'<xfa:datasets xmlns:xfa="{DATA_NS}"><xfa:data>{record_xml}</xfa:data></xfa:datasets>').encode("utf-8")


# --- Hand-made forms ---------------------------------------------------------------------------------------------


def text_field(name, w, h, x=None, y=None, caption=None, bind=None, picture=None, ui="textEdit", extra=""):
    pos = (f' x="{x}" y="{y}"' if x is not None else "")
    cap = f'<caption reserve="0.8in"><value><text>{caption}</text></value></caption>' if caption else ""
    bnd = f'<bind {bind}/>' if bind else ""
    fmt = f"<format><picture>{picture}</picture></format>" if picture else ""
    return (f'<field name="{name}" w="{w}" h="{h}"{pos}><ui><{ui}/></ui><font typeface="Arial" size="9pt"/>'
            f'<margin topInset="1pt" bottomInset="1pt" leftInset="2pt" rightInset="2pt"/>{cap}{bnd}{fmt}{extra}</field>')


def draw_text(name, text, w, h, x=None, y=None, extra=""):
    pos = (f' x="{x}" y="{y}"' if x is not None else "")
    return (f'<draw name="{name}" w="{w}" h="{h}"{pos}><value><text>{text}</text></value>'
            f'<font typeface="Arial" size="9pt"/>{extra}</draw>')


INVOICE_ITEMS = 40


def invoice():
    rows = "".join(
        f"<Item><Qty>{i + 1}</Qty><Description>Item number {i + 1:02d}</Description><Amount>{(i + 1) * 12.5:.2f}</Amount></Item>"
        for i in range(INVOICE_ITEMS))
    record = (
        "<form1><CompanyName>Example Supplies Inc</CompanyName>"
        "<Header><CustomerName>Ada Lovelace</CustomerName><CustomerCity>London</CustomerCity><InvoiceDate>2024-03-05</InvoiceDate></Header>"
        f"<Items>{rows}</Items>"
        "<Totals><Total>10250.00</Total></Totals>"
        "<Meta><Reference>REF-2024-0042</Reference></Meta>"
        "<Hidden><Secret>SHOULD-NOT-APPEAR</Secret></Hidden>"
        "</form1>")
    template = f"""<template xmlns="{TEMPLATE_NS}">
<subform name="form1" layout="tb" locale="en_US">
  <pageSet>
    <pageArea name="Main" id="Main">
      <contentArea x="0.5in" y="1in" w="7.5in" h="9in"/>
      <medium stock="letter" short="8.5in" long="11in"/>
      {draw_text("HeaderText", "Synthetic Invoice Header", "5in", "0.3in", "0.5in", "0.4in")}
      {text_field("CompanyName", "2.5in", "0.3in", "5.5in", "0.4in", bind='match="global"')}
    </pageArea>
    <pageArea name="Last" id="Last">
      <contentArea x="0.5in" y="1in" w="7.5in" h="9in"/>
      <medium stock="letter" short="8.5in" long="11in"/>
      {draw_text("LastHeader", "Final Page Header", "5in", "0.3in", "0.5in", "0.4in")}
    </pageArea>
  </pageSet>
  <subform name="Header" w="7.5in" h="1in">
    {text_field("CustomerName", "3.5in", "0.3in", "0in", "0in", caption="Customer")}
    {text_field("CustomerCity", "3.5in", "0.3in", "3.8in", "0in", caption="City")}
    {text_field("InvoiceDate", "3.5in", "0.3in", "0in", "0.5in", caption="Date", picture="date{MMMM D, YYYY}", ui="dateTimeEdit")}
    {text_field("CompanyName", "3.5in", "0.3in", "3.8in", "0.5in", bind='match="global"')}
  </subform>
  <subform name="Items" layout="table" columnWidths="1in 4in 2.5in" w="7.5in">
    <overflow leader="HeaderRow"/>
    <subform name="HeaderRow" layout="row" h="0.3in">
      {draw_text("QtyHeader", "Qty", "1in", "0.3in", extra='<border><edge/></border>')}
      {draw_text("DescriptionHeader", "Description", "4in", "0.3in", extra='<border><edge/></border>')}
      {draw_text("AmountHeader", "Amount", "2.5in", "0.3in", extra='<border><edge/></border>')}
    </subform>
    <subform name="Item" layout="row" h="0.3in">
      <occur min="0" max="-1"/>
      {text_field("Qty", "1in", "0.3in", ui="numericEdit")}
      {text_field("Description", "4in", "0.3in")}
      {text_field("Amount", "2.5in", "0.3in", picture="num{$z,zz9.99}", ui="numericEdit")}
    </subform>
  </subform>
  <subform name="Totals" w="7.5in" h="0.5in">
    {text_field("Total", "3in", "0.3in", "4.5in", "0in", caption="Total", picture="num{$zz,zz9.99}", ui="numericEdit")}
    {text_field("Ref", "3in", "0.3in", "0in", "0in", bind='match="dataRef" ref="$record.Meta.Reference"')}
  </subform>
  <subform name="Hidden" w="7.5in" h="0.5in" presence="hidden">
    {text_field("Secret", "3in", "0.3in", "0in", "0in")}
  </subform>
  <subform name="Notes" w="7.5in" h="2in">
    <breakBefore targetType="pageArea" target="#Last"/>
    <draw name="Terms" w="7.5in" h="1.5in" x="0in" y="0in">
      <value><exData contentType="text/html"><body xmlns="http://www.w3.org/1999/xhtml"><p>Payment is due within <b>30 days</b>.</p><p style="font-style:italic">Thank you for your business.</p></body></exData></value>
      <font typeface="Arial" size="10pt"/>
    </draw>
    <draw name="PrintButton" w="2in" h="0.3in" x="0in" y="1.6in" relevant="-print"><value><text>NOT-PRINTED</text></value></draw>
  </subform>
</subform>
</template>""".encode("utf-8")
    return template, datasets(record)


LAYOUT_ENTRIES = 30


def layouts():
    small = "".join(text_field(f"Cell{i:02d}", "2.4in", "0.4in") for i in range(12))
    entries = "".join(f"<Entry><Label>Entry {i + 1:02d}</Label></Entry>" for i in range(LAYOUT_ENTRIES))
    cells = "".join(f"<Cell{i:02d}>C{i:02d}</Cell{i:02d}>" for i in range(12))
    logo = base64.b64encode(png(40, 20)).decode("ascii")
    record = (
        f"<form1><Grid>{cells}</Grid>"
        "<Widgets><Agree>1</Agree><Choice>B</Choice><Color>g</Color><Zip>12345</Zip><Amount>-42.5</Amount></Widgets>"
        f"<List>{entries}</List></form1>")
    template = f"""<template xmlns="{TEMPLATE_NS}">
<subform name="form1" layout="tb" locale="en_US">
  <pageSet>
    <pageArea name="Columns" id="Columns">
      <contentArea name="Left" x="0.5in" y="1in" w="3.6in" h="9in"/>
      <contentArea name="Right" x="4.4in" y="1in" w="3.6in" h="9in"/>
      <medium stock="letter" short="8.5in" long="11in"/>
    </pageArea>
    <pageArea name="Wide" id="Wide">
      <contentArea x="0.5in" y="0.5in" w="7.5in" h="10in"/>
      <medium stock="letter" short="8.5in" long="11in"/>
      {draw_text("WideHeader", "Wide Page", "3in", "0.3in", "0.5in", "0.1in")}
    </pageArea>
  </pageSet>
  <subform name="Grid" layout="lr-tb" w="7.5in">
    <break before="pageArea" beforeTarget="#Wide"/>
    {small}
  </subform>
  <subform name="Widgets" w="7.5in" h="3in">
    <field name="Agree" x="0in" y="0in" w="2in" h="0.3in"><ui><checkButton size="10pt"><border><edge/></border></checkButton></ui>
      <items><integer>1</integer><integer>0</integer></items><caption placement="right" reserve="1.5in"><value><text>I agree</text></value></caption><font typeface="Arial" size="9pt"/></field>
    <exclGroup name="Choice" x="2.5in" y="0in" w="3in" h="0.3in" layout="lr-tb">
      <field name="A" w="1in" h="0.3in"><ui><checkButton shape="round" size="10pt"/></ui><items><text>A</text></items><caption placement="right" reserve="0.6in"><value><text>Alpha</text></value></caption></field>
      <field name="B" w="1in" h="0.3in"><ui><checkButton shape="round" size="10pt"/></ui><items><text>B</text></items><caption placement="right" reserve="0.6in"><value><text>Beta</text></value></caption></field>
    </exclGroup>
    <field name="Color" x="0in" y="0.5in" w="2in" h="0.3in"><ui><choiceList/></ui><font typeface="Arial" size="9pt"/>
      <items><text>Red</text><text>Green</text></items><items save="1" presence="hidden"><text>r</text><text>g</text></items></field>
    <field name="Zip" x="2.5in" y="0.5in" w="1.5in" h="0.3in"><ui><textEdit><comb numberOfCells="5"/></textEdit></ui><font typeface="Courier" size="10pt"/></field>
    {text_field("Amount", "2in", "0.3in", "4.5in", "0.5in", picture="num{z,zz9.99s}|num{z,zz9.99}", ui="numericEdit")}
    {text_field("Unbound", "2in", "0.3in", "0in", "1in", bind='match="none"', extra="<value><text>Default value</text></value>")}
    <draw name="Rotated" x="3in" y="2.9in" w="1in" h="0.3in" rotate="90"><value><text>Up</text></value><font typeface="Arial" size="9pt"/></draw>
    <draw name="Logo" x="5in" y="1in" w="1in" h="0.5in"><value><image contentType="image/png">{logo}</image></value></draw>
    <draw name="Rule" x="0in" y="1.6in" w="7.5in" h="0in"><value><line><edge thickness="1pt"/></line></value></draw>
    <draw name="Box" x="0in" y="1.8in" w="1in" h="0.5in"><value><rectangle><edge thickness="1pt"/><fill><color value="200,200,255"/></fill></rectangle></value></draw>
    <draw name="Circle" x="1.5in" y="1.8in" w="0.5in" h="0.5in"><value><arc circular="1"><edge/></arc></value></draw>
    <draw name="Centered" x="5in" y="2.5in" w="2in" h="0.4in" anchorType="middleCenter"><value><text>Centered</text></value><para hAlign="center"/></draw>
  </subform>
  <subform name="List" layout="tb" w="3.6in">
    <breakBefore targetType="pageArea" target="#Columns"/>
    <subform name="Entry" w="3.6in" h="0.5in">
      <occur min="0" max="-1"/>
      {text_field("Label", "3.4in", "0.4in", "0in", "0in")}
    </subform>
  </subform>
</subform>
</template>""".encode("utf-8")
    return template, datasets(record)


# --- The oracle --------------------------------------------------------------------------------------------------


def local(tag):
    return tag.split("}", 1)[-1]


def kids(element, name=None):
    return [c for c in element if isinstance(c.tag, str) and (name is None or local(c.tag) == name)]


def child(element, name):
    found = kids(element, name)
    return found[0] if found else None


def inches(text):
    """A measurement in inches."""
    if text is None:
        return None
    text = text.strip()
    for unit, factor in (("in", 1.0), ("mm", 1 / 25.4), ("cm", 1 / 2.54), ("pt", 1 / 72.0)):
        if text.endswith(unit):
            return float(text[: -len(unit)]) * factor
    return float(text)


def hidden(element):
    return element.get("presence") in ("hidden", "inactive") or "-print" in (element.get("relevant") or "").split()


def page_areas(root):
    page_set = child(root, "pageSet")
    areas = {}
    for area in kids(page_set, "pageArea"):
        areas[area.get("id")] = [inches(c.get("h")) for c in kids(area, "contentArea")]
    return [a.get("id") for a in kids(page_set, "pageArea")], areas


class Paginator:
    """Places fixed-height objects of a top-to-bottom flow into content areas and pages."""

    def __init__(self, order, areas):
        self.areas = areas
        self.page_area = order[0]
        self.page = 1
        self.area = 0
        self.cursor = 0.0
        self.placed = False

    @property
    def height(self):
        return self.areas[self.page_area][self.area]

    def next_area(self):
        if self.area + 1 < len(self.areas[self.page_area]):
            self.area += 1
        else:
            self.page += 1
            self.area = 0
        self.cursor = 0.0

    def break_to(self, target):
        if not self.placed and self.area == 0 and self.cursor == 0:
            self.page_area = target
            return
        self.page += 1
        self.page_area = target
        self.area = 0
        self.cursor = 0.0

    def place(self, height, leader=0.0):
        """Places an object; returns the page it lands on. A leader repeats at the top of each new content area."""
        if self.cursor + height > self.height + 1e-6 and self.cursor > 0:
            self.next_area()
            self.cursor += leader
        self.cursor += height
        self.placed = True
        return self.page


def data_value(record, path):
    node = record
    for name in path:
        node = next((c for c in node if local(c.tag) == name), None)
        if node is None:
            return None
    return node.text or ""


def expect_invoice(template_xml, data_xml):
    """The invoice's values in order with their pages, from its fixed heights."""
    root = child(ET.fromstring(template_xml), "subform")
    record = kids(child(ET.fromstring(data_xml), "data"))[0]
    order, areas = page_areas(root)
    pager = Paginator(order, areas)
    values = []
    company = data_value(record, ["CompanyName"])
    pages_with_main = set()
    for sub in kids(root, "subform"):
        if hidden(sub):
            continue
        before = child(sub, "breakBefore")
        if before is not None:
            pager.break_to(before.get("target").lstrip("#"))
        name = sub.get("name")
        if sub.get("layout") == "table":
            header = child(sub, "subform")
            row_h = inches(header.get("h"))
            page = pager.place(row_h)
            pages_with_main.add(page)
            for item in [c for c in kids(child(record, name)) if local(c.tag) == "Item"]:
                page = pager.place(row_h, leader=row_h)
                pages_with_main.add(page)
                qty, desc, amount = (item[0].text, item[1].text, float(item[2].text))
                values.append((qty, page))
                values.append((desc, page))
                values.append((f"${amount:,.2f}", page))
            continue
        page = pager.place(inches(sub.get("h")))
        if pager.page_area == "Main":
            pages_with_main.add(page)
        if name == "Header":
            values += [(data_value(record, ["Header", "CustomerName"]), page), (data_value(record, ["Header", "CustomerCity"]), page),
                       ("March 5, 2024", page), (company, page)]
        elif name == "Totals":
            total = float(data_value(record, ["Totals", "Total"]))
            values += [(f"${total:,.2f}", page), (data_value(record, ["Meta", "Reference"]), page)]
        elif name == "Notes":
            values += [("Final Page Header", page), ("Payment is due within 30 days.", page), ("Thank you for your business.", page)]
    for page in sorted(pages_with_main):
        values.append(("Synthetic Invoice Header", page))
    return {"pages": pager.page, "values": [{"text": t, "page": p} for t, p in values],
            "absent": ["SHOULD-NOT-APPEAR", "NOT-PRINTED"], "ordered": False}


def expect_layouts(template_xml, data_xml):
    root = child(ET.fromstring(template_xml), "subform")
    record = kids(child(ET.fromstring(data_xml), "data"))[0]
    order, areas = page_areas(root)
    pager = Paginator(order, areas)
    values = []
    for sub in kids(root, "subform"):
        legacy = child(sub, "break")
        before = child(sub, "breakBefore")
        if legacy is not None and legacy.get("before") == "pageArea":
            pager.break_to(legacy.get("beforeTarget").lstrip("#"))
        if before is not None:
            pager.break_to(before.get("target").lstrip("#"))
        name = sub.get("name")
        if name == "Grid":
            fields = kids(sub, "field")
            per_line = int(7.5 // inches(fields[0].get("w")))
            lines = -(-len(fields) // per_line)
            page = pager.place(lines * inches(fields[0].get("h")))
            values.append(("Wide Page", page))
            values += [(f"C{i:02d}", page) for i in range(len(fields))]
        elif name == "Widgets":
            page = pager.place(inches(sub.get("h")))
            values += [("I agree", page), ("Alpha", page), ("Beta", page), ("Green", page), ("42.50-", page), ("Default value", page),
                       ("Up", page), ("Centered", page)]
        elif name == "List":
            entry = child(sub, "subform")
            for e in kids(child(record, "List")):
                page = pager.place(inches(entry.get("h")))
                values.append((e[0].text, page))
    return {"pages": pager.page, "values": [{"text": t, "page": p} for t, p in values], "absent": [], "ordered": False}


# --- The government forms ----------------------------------------------------------------------------------------


def read_xfa(path):
    pdf = pikepdf.open(path)
    xfa = pdf.Root.AcroForm.XFA
    packets = {}
    for i in range(0, len(xfa), 2):
        packets[str(xfa[i])] = xfa[i + 1].read_bytes()
    return packets


def text_fields(subform, path, out, counter):
    """Fills the data of a subform's text fields bound by name; returns its data XML."""
    xml = ""
    for c in kids(subform):
        name = local(c.tag)
        if name in ("subform", "subformSet", "area", "exclGroup") and not hidden(c):
            inner = text_fields(c, path, out, counter)
            bind = child(c, "bind")
            if name == "subform" and c.get("name") and (bind is None or bind.get("match", "once") == "once"):
                xml += f"<{c.get('name')}>{inner}</{c.get('name')}>"
            else:
                xml += inner
        elif name == "field" and c.get("name") and not hidden(c):
            ui = child(child(c, "ui"), "textEdit") if child(c, "ui") is not None else None
            bind = child(c, "bind")
            if ui is None or child(ui, "comb") is not None or child(c, "format") is not None:
                continue
            if bind is not None and bind.get("match", "once") != "once":
                continue
            if child(c, "value") is not None and child(child(c, "value"), "exData") is not None:
                continue
            counter[0] += 1
            token = f"V{counter[0]:03d}"
            xml += f"<{c.get('name')}>{token}</{c.get('name')}>"
            out.append(token)
    return xml


def corpus(directory, expected):
    for source, target in CORPUS.items():
        packets = read_xfa(os.path.join(directory, source))
        template = ET.fromstring(packets["template"])
        root = child(template, "subform")
        pages = []
        counter = [0]
        record = ""
        # Each top-level subform is a page (the forms' pages are page-sized positioned subforms).
        for sub in kids(root, "subform"):
            if hidden(sub):
                continue
            values = []
            inner = text_fields(sub, [], values, counter)
            bind = child(sub, "bind")
            if sub.get("name") and (bind is None or bind.get("match", "once") == "once"):
                record += f"<{sub.get('name')}>{inner}</{sub.get('name')}>"
            else:
                record += inner
            pages.append(values)
        data = datasets(f"<{root.get('name')}>{record}</{root.get('name')}>")
        write_pdf(os.path.join(HERE, target),
                  xdp_packets(packets["template"], data, packets.get("config"), packets.get("localeSet")),
                  f"{source} (public domain), with generated data")
        expected[target] = {
            "pages": len(pages),
            "values": [{"text": v, "page": i + 1} for i, values in enumerate(pages) for v in values],
            "absent": [],
            "ordered": False,
        }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--corpus", help="the folder with the downloaded government forms")
    args = parser.parse_args()
    path = os.path.join(HERE, "expected.json")
    expected = json.load(open(path)) if os.path.exists(path) else {}
    for name, make, expect in (("invoice.pdf", invoice, expect_invoice), ("layouts.pdf", layouts, expect_layouts)):
        template, data = make()
        write_pdf(os.path.join(HERE, name), xdp_packets(template, data), name)
        expected[name] = expect(template, data)
    if args.corpus:
        corpus(args.corpus, expected)
    with open(path, "w") as f:
        json.dump(expected, f, indent=1, sort_keys=True)
        f.write("\n")


if __name__ == "__main__":
    sys.exit(main())
