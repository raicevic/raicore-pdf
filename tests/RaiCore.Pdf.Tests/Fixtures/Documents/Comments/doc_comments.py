"""An independent reader of the comments of Word 97-2003 (.doc) files, for make-expected.py: olefile for the compound
file and a walk of the [MS-DOC] structures written from the specification (FIB, piece table, PlcfandRef with its
ATRDPre10 records, GrpXstAtnOwners, AtrdExtra with ATRDPost10, SttbfAtnBkmk with PlcfAtnBkf and PlcfAtnBkl,
PlcfandTxt). Not used by the library."""
import struct

import olefile


def u16(b, o):
    return struct.unpack_from("<H", b, o)[0]


def u32(b, o):
    return struct.unpack_from("<I", b, o)[0]


def i32(b, o):
    return struct.unpack_from("<i", b, o)[0]


def dttm(value):
    """A DTTM as "yyyy-MM-dd HH:mm", or None."""
    if value == 0:
        return None
    minute, hour, day = value & 0x3F, (value >> 6) & 0x1F, (value >> 11) & 0x1F
    month, year = (value >> 16) & 0xF, 1900 + ((value >> 20) & 0x1FF)
    if minute > 59 or hour > 23 or not 1 <= month <= 12 or day < 1:
        return None
    return f"{year:04d}-{month:02d}-{day:02d} {hour:02d}:{minute:02d}"


def read(path):
    """The comments in PlcfandRef order: author, initials, date, text, the text they are about, parent index, and
    the character position of their reference."""
    ole = olefile.OleFileIO(path)
    word = ole.openstream("WordDocument").read()
    flags = u16(word, 10)
    table = ole.openstream("1Table" if flags & 0x0200 else "0Table").read()
    ccp_text = i32(word, 76)
    ccp = [i32(word, 76 + 4 * i) for i in range(8)]  # text, ftn, hdd, mcr, atn, edn, txbx, hdrtxbx
    count = u16(word, 152)
    pairs = [(u32(word, 154 + 8 * i), u32(word, 158 + 8 * i)) for i in range(count)]

    def data(index):
        if index >= len(pairs):
            return b""
        fc, lcb = pairs[index]
        return table[fc:fc + lcb]

    # The text by character position, through the piece table (Clx).
    clx = data(33)
    pos = 0
    while clx[pos] == 1:
        pos += 3 + u16(clx, pos + 1)
    assert clx[pos] == 2
    lcb = u32(clx, pos + 1)
    plc = clx[pos + 5:pos + 5 + lcb]
    n = (len(plc) - 4) // 12
    pieces = []
    for i in range(n):
        cp0, cp1 = i32(plc, 4 * i), i32(plc, 4 * (i + 1))
        fc = u32(plc, 4 * (n + 1) + 8 * i + 2)
        compressed = bool(fc & 0x40000000)
        fc &= 0x3FFFFFFF
        if compressed:
            fc //= 2
            text = word[fc:fc + (cp1 - cp0)].decode("cp1252", "replace")
        else:
            text = word[fc:fc + 2 * (cp1 - cp0)].decode("utf-16-le", "replace")
        pieces.append(text)
    text = "".join(pieces)

    refs = data(4)
    count = (len(refs) - 4) // 34 if len(refs) >= 4 else 0
    if count == 0:
        return []
    owners, o, owner_data = [], 0, data(36)
    while o + 2 <= len(owner_data):
        length = u16(owner_data, o)
        owners.append(owner_data[o + 2:o + 2 + 2 * length].decode("utf-16-le"))
        o += 2 + 2 * length

    # Bookmarks: tags (ATNBE.lTag) with their start CP and the index of their end CP.
    ranges = {}
    sttb = data(37)
    if len(sttb) >= 6 and u16(sttb, 0) == 0xFFFF:
        n_bk, extra = u16(sttb, 2), u16(sttb, 4)
        bkf, bkl = data(42), data(43)
        n_bkf = (len(bkf) - 4) // 8
        n_bkl = (len(bkl) - 4) // 4
        o = 6
        for i in range(min(n_bk, n_bkf)):
            o += 2 + 2 * u16(sttb, o)
            tag = i32(sttb, o + 2)
            o += extra
            start = i32(bkf, 4 * i)
            end_index = u16(bkf, 4 * (n_bkf + 1) + 4 * i)
            if end_index < n_bkl:
                ranges[tag] = (start, i32(bkl, 4 * end_index))

    texts = data(5)
    atn_start = ccp[0] + ccp[1] + ccp[2] + ccp[3]
    extra = data(112)
    comments = []
    for i in range(count):
        cp = i32(refs, 4 * i)
        atrd = refs[4 * (count + 1) + 30 * i:4 * (count + 1) + 30 * (i + 1)]
        initials = atrd[2:2 + 2 * min(u16(atrd, 0), 9)].decode("utf-16-le")
        owner = u16(atrd, 20)
        tag = i32(atrd, 26)
        t0, t1 = i32(texts, 4 * i), i32(texts, 4 * (i + 1))
        body = text[atn_start + t0:atn_start + t1]
        # Paragraph marks end paragraphs; the annotation reference mark (0x05) and other controls are not text.
        paragraphs = ["".join(c for c in p if c >= " ") for p in body.split("\r")]
        while paragraphs and paragraphs[-1] == "":
            paragraphs.pop()
        date, parent = None, None
        if len(extra) >= 18 * (i + 1):
            date = dttm(u32(extra, 18 * i))
            depth, offset = i32(extra, 18 * i + 6), i32(extra, 18 * i + 10)
            if depth > 0 and 0 <= i + offset < count and offset != 0:
                parent = i + offset
        about = None
        if tag != -1 and tag in ranges:
            s, e = ranges[tag]
            about = " ".join("".join(c if c >= " " else " " for c in text[s:e]).split())
        comments.append({
            "author": owners[owner] if owner < len(owners) and owners[owner] else None,
            "initials": initials.strip("\0 ") or None,
            "date": date,
            "text": "\n".join(p.strip() for p in paragraphs).strip("\n"),
            "about": about,
            "parent": parent,
            "reference": cp,
        })
    return comments
