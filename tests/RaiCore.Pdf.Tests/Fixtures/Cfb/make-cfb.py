"""Generates the compound file fixtures with a small writer of its own ([MS-CFB]) and records what an independent reader
(olefile) finds in them in expected.json: every stream's path, size and SHA-256, and the summary information.
Run with a venv that has olefile; from this folder: python make-cfb.py"""
import hashlib
import json
import math
import struct
import uuid

import olefile

FREE, END, FATSECT, DIFSECT, NOSTREAM = 0xFFFFFFFF, 0xFFFFFFFE, 0xFFFFFFFD, 0xFFFFFFFC, 0xFFFFFFFF
CUTOFF, MINI = 4096, 64


def data(name, size):
    """Deterministic bytes that differ in every position, so a wrong sector shows."""
    out = bytearray()
    block = name.encode()
    while len(out) < size:
        block = hashlib.sha256(block).digest()
        out += block
    return bytes(out[:size])


def property_set(fmtid, code_page, props):
    """A property set stream with one set: props are (id, vt, value)."""
    def value(vt, v):
        if vt == 0x02:
            return struct.pack('<HHh2x', vt, 0, v)
        if vt == 0x03:
            return struct.pack('<HHi', vt, 0, v)
        if vt == 0x1E:
            raw = v.encode('utf-16-le' if code_page == 1200 else 'cp%d' % code_page) + (b'\0\0' if code_page == 1200 else b'\0')
            body = struct.pack('<HHI', vt, 0, len(raw)) + raw
            return body + b'\0' * (-len(body) % 4)
        if vt == 0x1F:
            raw = (v + '\0').encode('utf-16-le')
            body = struct.pack('<HHI', vt, 0, len(raw) // 2) + raw
            return body + b'\0' * (-len(body) % 4)
        if vt == 0x40:
            return struct.pack('<HHQ', vt, 0, v)
        if vt == 0x0B:
            return struct.pack('<HHH2x', vt, 0, 0xFFFF if v else 0)
        raise ValueError(vt)

    props = [(1, 0x02, code_page)] + props
    values = [value(vt, v) for _, vt, v in props]
    offset = 8 + 8 * len(props)
    table, body = b'', b''
    for (pid, _, _), v in zip(props, values):
        table += struct.pack('<II', pid, offset + len(body))
        body += v
    section = struct.pack('<II', 8 + len(table) + len(body), len(props)) + table + body
    header = struct.pack('<HHI', 0xFFFE, 0, 0x00020006) + b'\0' * 16 + struct.pack('<I', 1)
    return header + uuid.UUID(fmtid).bytes_le + struct.pack('<I', 48) + section


def write_cfb(path, version, tree, min_fat_sectors=0):
    """tree: {name: bytes | dict} under the root. Writes a compound file of version 3 or 4."""
    S = 512 if version == 3 else 4096
    per_sector = S // 4

    # Directory entries in breadth-first order; children of each storage as a balanced tree by CFB name order.
    entries = [{'name': 'Root Entry', 'type': 5, 'children': list(tree.items())}]
    queue = [0]
    while queue:
        index = queue.pop(0)
        kids = []
        for name, content in entries[index].pop('children'):
            entry = {'name': name, 'type': 1 if isinstance(content, dict) else 2}
            if isinstance(content, dict):
                entry['children'] = list(content.items())
                queue.append(len(entries))
            else:
                entry['data'] = content
            kids.append(len(entries))
            entries.append(entry)
        entries[index]['kids'] = kids
    for entry in entries:
        entry.setdefault('left', NOSTREAM)
        entry.setdefault('right', NOSTREAM)
        entry.setdefault('child', NOSTREAM)
    key = lambda i: (len(entries[i]['name']), entries[i]['name'].upper())
    def balance(ids):
        if not ids:
            return NOSTREAM
        mid = len(ids) // 2
        entries[ids[mid]]['left'] = balance(ids[:mid])
        entries[ids[mid]]['right'] = balance(ids[mid + 1:])
        return ids[mid]
    for entry in entries:
        if 'kids' in entry:
            entry['child'] = balance(sorted(entry.pop('kids'), key=key))

    # The mini stream: small streams in 64-byte sectors.
    mini_stream, mini_fat = b'', []
    for entry in entries:
        if entry['type'] == 2 and len(entry['data']) < CUTOFF and entry['data']:
            count = math.ceil(len(entry['data']) / MINI)
            entry['start'] = len(mini_fat)
            mini_fat += [len(mini_fat) + i + 1 for i in range(count - 1)] + [END]
            mini_stream += entry['data'] + b'\0' * (-len(entry['data']) % MINI)

    directory_sectors = math.ceil(len(entries) * 128 / S)
    mini_fat_sectors = math.ceil(len(mini_fat) * 4 / S)
    mini_stream_sectors = math.ceil(len(mini_stream) / S)
    large = [e for e in entries if e['type'] == 2 and len(e['data']) >= CUTOFF]
    large_sectors = sum(math.ceil(len(e['data']) / S) for e in large)
    data_sectors = directory_sectors + mini_fat_sectors + mini_stream_sectors + large_sectors

    fat_n, difat_n = max(1, min_fat_sectors), 0
    while True:
        need_fat = max(min_fat_sectors, math.ceil((data_sectors + fat_n + difat_n) / per_sector))
        need_difat = math.ceil(max(0, need_fat - 109) / (per_sector - 1))
        if (need_fat, need_difat) == (fat_n, difat_n):
            break
        fat_n, difat_n = need_fat, need_difat

    total = fat_n + difat_n + data_sectors
    fat = [FREE] * (fat_n * per_sector)
    next_sector = 0
    def chain(count):
        nonlocal next_sector
        if count == 0:
            return END
        start = next_sector
        for i in range(count):
            fat[start + i] = start + i + 1 if i < count - 1 else END
        next_sector += count
        return start

    fat_sectors = list(range(fat_n))
    for s in fat_sectors:
        fat[s] = FATSECT
    next_sector = fat_n
    difat_sectors = list(range(next_sector, next_sector + difat_n))
    for s in difat_sectors:
        fat[s] = DIFSECT
    next_sector += difat_n
    first_directory = chain(directory_sectors)
    first_mini_fat = chain(mini_fat_sectors)
    entries[0]['start'] = chain(mini_stream_sectors)
    entries[0]['data'] = mini_stream
    for entry in large:
        entry['start'] = chain(math.ceil(len(entry['data']) / S))
    assert next_sector == total

    sectors = [bytearray(S) for _ in range(total)]
    def put(start, payload):
        s = start
        for offset in range(0, len(payload), S):
            sectors[s][:len(payload[offset:offset + S])] = payload[offset:offset + S]
            s = fat[s]
    fat_bytes = b''.join(struct.pack('<I', v) for v in fat)
    for i, s in enumerate(fat_sectors):
        sectors[s][:] = fat_bytes[i * S:(i + 1) * S]
    rest = fat_sectors[109:]
    for i, s in enumerate(difat_sectors):
        part = rest[i * (per_sector - 1):(i + 1) * (per_sector - 1)]
        part += [FREE] * (per_sector - 1 - len(part))
        part.append(difat_sectors[i + 1] if i + 1 < len(difat_sectors) else END)
        sectors[s][:] = b''.join(struct.pack('<I', v) for v in part)

    directory = b''
    for entry in entries:
        name = entry['name'].encode('utf-16-le') + b'\0\0'
        size = len(entry.get('data', b''))
        start = entry.get('start', END if entry['type'] != 1 else 0)
        directory += (name.ljust(64, b'\0') + struct.pack('<HBBIII', len(name), entry['type'], 1, entry['left'], entry['right'], entry['child'])
                      + b'\0' * 16 + struct.pack('<IQQIQ', 0, 0, 0, start, size))
    directory += (b'\0' * 64 + struct.pack('<HBBIII', 0, 0, 0, NOSTREAM, NOSTREAM, NOSTREAM) + b'\0' * 44) * ((-len(entries)) % (S // 128))
    put(first_directory, directory)
    if mini_fat:
        put(first_mini_fat, b''.join(struct.pack('<I', v) for v in mini_fat) + struct.pack('<I', FREE) * ((-len(mini_fat)) % per_sector))
    if mini_stream:
        put(entries[0]['start'], mini_stream)
    for entry in large:
        put(entry['start'], entry['data'])

    header_difat = fat_sectors[:109] + [FREE] * (109 - min(109, fat_n))
    header = (bytes([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]) + b'\0' * 16
              + struct.pack('<HHHHH6xIIIIIIIII', 0x3E, version, 0xFFFE, 9 if version == 3 else 12, 6,
                            directory_sectors if version == 4 else 0, fat_n, first_directory, 0, CUTOFF,
                            first_mini_fat if mini_fat else END, mini_fat_sectors,
                            difat_sectors[0] if difat_sectors else END, difat_n)
              + b''.join(struct.pack('<I', v) for v in header_difat))
    with open(path, 'wb') as f:
        f.write(header.ljust(S, b'\0'))
        for sector in sectors:
            f.write(sector)


def summary():
    return property_set('F29F85E0-4FF9-1068-AB91-08002B27B3D9', 1252, [
        (2, 0x1E, 'Résumé of the quarter'),
        (4, 0x1E, 'Ada Lovelace'),
        (12, 0x40, 133_000_000_000_000_000),  # A FILETIME in 2022.
    ])


def document_summary():
    return property_set('D5CDD502-2E9C-101B-9397-08002B2CF9AE', 1200, [
        (2, 0x1E, 'Category ünïcode'),
        (15, 0x1E, 'Analytical Engines Ltd'),
        (0x17, 0x03, 0x000C0000),
        (0x13, 0x0B, True),
    ])


def tree():
    return {
        'Small': data('Small', 100),
        'Large': data('Large', 10000),
        'Sub': {'Inner': data('Inner', 5000), 'Deeper': {'Leaf': data('Leaf', 33)}},
        'Mini1': data('Mini1', 4000),
        'Mini2': data('Mini2', 4000),
        'Mini3': data('Mini3', 4000),
        'Empty': b'',
        'Exactly4096': data('Exactly4096', 4096),
        '\x05SummaryInformation': summary(),
        '\x05DocumentSummaryInformation': document_summary(),
    }


files = {
    'v3-basic.cfb': (3, tree(), 0),
    'v3-difat.cfb': (3, {'Data': data('Data', 6000), 'Note': data('Note', 70)}, 240),  # Two DIFAT sectors.
    'v4-basic.cfb': (4, tree(), 0),
}
expected = {}
for name, (version, content, fat_min) in files.items():
    write_cfb(name, version, content, fat_min)
    ole = olefile.OleFileIO(name)
    streams = {}
    for entry in ole.listdir(streams=True, storages=False):
        payload = ole.openstream(entry).read()
        streams['/'.join(entry)] = {'size': len(payload), 'sha256': hashlib.sha256(payload).hexdigest()}
    meta = ole.get_metadata()
    expected[name] = {
        'version': version,
        'fat_sectors': struct.unpack_from('<I', open(name, 'rb').read(), 44)[0],
        'streams': streams,
        'title': meta.title.decode('cp1252') if meta.title else None,
        'author': meta.author.decode('cp1252') if meta.author else None,
        'created': meta.create_time.isoformat() if meta.create_time else None,
    }
    ole.close()
with open('expected.json', 'w') as f:
    json.dump(expected, f, indent=2, sort_keys=True)
print(json.dumps({k: len(v['streams']) for k, v in expected.items()}))
