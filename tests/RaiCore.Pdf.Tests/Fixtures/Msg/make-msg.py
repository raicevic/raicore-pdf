"""Generates the Outlook message fixtures ([MS-OXMSG]) with the compound file writer of ../Cfb/make-cfb.py, an
equivalent .eml for each (Python's email package), and expected.json with what extract-msg (an independent reader)
finds in the .msg files. Run with a venv that has olefile, extract-msg and compressed_rtf; from this folder:
python make-msg.py"""
import datetime
import email.policy
import email.utils
import importlib.util
import json
import os
import struct
import zlib
from email.message import EmailMessage

import compressed_rtf
import extract_msg

spec = importlib.util.spec_from_file_location('cfb', os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'Cfb', 'make-cfb.py'))
cfb = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cfb)

UNICODE, STRING8, BINARY, LONG, BOOLEAN, SYSTIME, OBJECT = 0x001F, 0x001E, 0x0102, 0x0003, 0x000B, 0x0040, 0x000D
DATE = datetime.datetime(2026, 3, 14, 9, 26, 53, tzinfo=datetime.timezone.utc)


def filetime(when):
    return int((when - datetime.datetime(1601, 1, 1, tzinfo=datetime.timezone.utc)).total_seconds() * 10_000_000)


def png(width, height, rgb):
    """A small solid PNG."""
    def chunk(kind, payload):
        return struct.pack('>I', len(payload)) + kind + payload + struct.pack('>I', zlib.crc32(kind + payload) & 0xFFFFFFFF)
    rows = b''.join(b'\0' + bytes(rgb) * width for _ in range(height))
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 2, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(rows)) + chunk(b'IEND', b''))


def storage(props, header, code_page=1252, children=None):
    """A message, recipient or attachment storage: props are (id, type, value)."""
    entries, streams = b'', {}
    for pid, ptype, value in props:
        tag = (pid << 16) | ptype
        if ptype in (UNICODE, STRING8, BINARY):
            # String streams leave out the terminating null; the size in the entry counts it ([MS-OXMSG] 2.1.2).
            raw = value.encode('utf-16-le') if ptype == UNICODE else (value.encode('cp%d' % code_page) if ptype == STRING8 else value)
            streams['__substg1.0_%04X%04X' % (pid, ptype)] = raw
            entries += struct.pack('<IIII', tag, 6, len(raw) + (2 if ptype == UNICODE else 1 if ptype == STRING8 else 0), 0)
        elif ptype == OBJECT:
            entries += struct.pack('<IIII', tag, 6, 0xFFFFFFFF, 0)
        elif ptype == SYSTIME:
            entries += struct.pack('<IIQ', tag, 6, value)
        else:
            entries += struct.pack('<IIiI', tag, 6, value, 0)
    tree = {'__properties_version1.0': header + entries}
    tree.update(streams)
    tree.update(children or {})
    return tree


def message(m, embedded=False):
    """The storage tree of a message described by a dict (see MESSAGES)."""
    u = STRING8 if m.get('ansi') else UNICODE
    props = [
        (0x001A, u, 'IPM.Note'),
        (0x0037, u, m['subject']),
        (0x0C1A, u, m.get('sender', m['from'])[0]), (0x5D01, u, m.get('sender', m['from'])[1]),
        (0x0042, u, m['from'][0]), (0x5D02, u, m['from'][1]),
        (0x0039, SYSTIME, filetime(DATE)),
        (0x3FFD, LONG, 1252 if m.get('ansi') else 65001),
        (0x3FDE, LONG, 65001),
        (0x1035, u, '<%s@example.com>' % m['name']),
    ]
    if 'text' in m:
        props.append((0x1000, u, m['text']))
    if 'html' in m:
        props.append((0x1013, BINARY, m['html'].encode('utf-8')))
    if 'rtf' in m:
        props.append((0x1009, BINARY, compressed_rtf.compress(m['rtf'].encode('latin-1'))))
    children = {}
    for i, (kind, name, address) in enumerate(m.get('recipients', [])):
        children['__recip_version1.0_#%08X' % i] = storage([
            (0x0C15, LONG, kind), (0x3001, u, name), (0x39FE, u, address), (0x3003, u, address),
        ], b'\0' * 8)
    for i, a in enumerate(m.get('attachments', [])):
        if 'message' in a:
            inner = message(a['message'], embedded=True)
            children['__attach_version1.0_#%08X' % i] = storage([
                (0x3705, LONG, 5), (0x3001, u, a['message']['subject']), (0x3701, OBJECT, None),
            ], b'\0' * 8, children={'__substg1.0_3701000D': inner})
        else:
            attach = [(0x3705, LONG, 1), (0x3707, u, a['name']), (0x3704, u, a['name'][:12]), (0x370E, u, a['type']), (0x3701, BINARY, a['data'])]
            if 'cid' in a:
                attach += [(0x3712, u, a['cid']), (0x7FFE, BOOLEAN, 1)]
            children['__attach_version1.0_#%08X' % i] = storage(attach, b'\0' * 8)
    recipients, attachments = len(m.get('recipients', [])), len(m.get('attachments', []))
    header = struct.pack('<8xIIII', recipients, attachments, recipients, attachments) + (b'' if embedded else b'\0' * 8)
    if not embedded:
        children['__nameid_version1.0'] = {'__substg1.0_00020102': b'', '__substg1.0_00030102': b'', '__substg1.0_00040102': b''}
    return storage(props, header, 1252 if m.get('ansi') else 65001, children)


def eml(m):
    """The same message as a MIME message, written by Python's email package."""
    out = EmailMessage(policy=email.policy.SMTP)
    out['From'] = email.utils.formataddr(m['from'])
    for kind, field in ((1, 'To'), (2, 'Cc')):
        people = [email.utils.formataddr((n, a)) for k, n, a in m.get('recipients', []) if k == kind]
        if people:
            out[field] = ', '.join(people)
    out['Date'] = email.utils.format_datetime(DATE)
    out['Subject'] = m['subject']
    html = m.get('html') or m.get('rtf_html')
    if html:
        out.set_content(html, subtype='html')
    else:
        out.set_content(m['text'])
    for a in m.get('attachments', []):
        if 'message' in a:
            out.add_attachment(eml(a['message']))
        elif 'cid' in a:
            continue  # Placed beside the HTML below.
        else:
            out.add_attachment(a['data'], *a['type'].split('/'), filename=a['name'])
    if any('cid' in a for a in m.get('attachments', [])):
        # Rebuild as multipart/related so the image stands beside the HTML, as Outlook sends it.
        related = EmailMessage(policy=email.policy.SMTP)
        related.set_content(html, subtype='html')
        for a in m['attachments']:
            if 'cid' in a:
                related.add_related(a['data'], *a['type'].split('/'), cid='<%s>' % a['cid'], filename=a['name'], disposition='inline')
        mixed = EmailMessage(policy=email.policy.SMTP)
        for field in ('From', 'To', 'Cc', 'Date', 'Subject'):
            if out[field]:
                mixed[field] = out[field]
        mixed.make_mixed()
        mixed.attach(related)
        for a in m['attachments']:
            if 'cid' not in a and 'message' not in a:
                mixed.add_attachment(a['data'], *a['type'].split('/'), filename=a['name'])
        return mixed
    return out


RTF_HTML = '<html><body><p>Hello from <b>RTF</b> &amp; HTML.</p><p>Second paragraph.</p></body></html>'
RTF = ('{\\rtf1\\ansi\\ansicpg1252\\fromhtml1 \\fbidis \\deff0{\\fonttbl{\\f0\\fswiss Arial;}}'
       '{\\*\\htmltag19 <html>}{\\*\\htmltag34 <body>}{\\*\\htmltag64 <p>}\\htmlrtf {\\htmlrtf0 Hello from '
       '{\\*\\htmltag84 <b>}\\htmlrtf {\\b \\htmlrtf0 RTF{\\*\\htmltag92 </b>}\\htmlrtf }\\htmlrtf0  &amp; HTML.'
       '{\\*\\htmltag72 </p>}\\htmlrtf \\par }\\htmlrtf0 {\\*\\htmltag64 <p>}\\htmlrtf {\\htmlrtf0 Second paragraph.'
       '{\\*\\htmltag72 </p>}\\htmlrtf \\par }\\htmlrtf0 {\\*\\htmltag42 </body>}{\\*\\htmltag27 </html>}}')

MESSAGES = {
    'delegate': {
        'name': 'delegate', 'subject': 'Sent on behalf', 'from': ('Ada Lovelace', 'ada@example.com'),
        'sender': ('Assistant', 'assistant@example.com'),
        'recipients': [(1, 'Charles Babbage', 'charles@example.com')], 'text': 'Sent by the assistant for Ada.',
    },
    'html': {
        'name': 'html', 'subject': 'Quarterly report \u2013 Q3 \u20ac',
        'from': ('Ada Lovelace', 'ada@example.com'),
        'recipients': [(1, 'Charles Babbage', 'charles@example.com'), (2, 'Mary Somerville', 'mary@example.org')],
        'text': 'The figures are in the chart below.',
        'html': '<html><body><h1>Quarterly report</h1><p>The figures are in the chart below.</p>'
                '<p><img src="cid:chart@example" alt="Chart"></p></body></html>',
        'attachments': [
            {'name': 'chart.png', 'type': 'image/png', 'data': png(40, 20, (200, 30, 30)), 'cid': 'chart@example'},
            {'name': 'notes \u2013 draft.txt', 'type': 'text/plain', 'data': 'Draft notes.\r\n'.encode()},
        ],
    },
    'rtf': {
        'name': 'rtf', 'subject': 'Body in compressed RTF',
        'from': ('Grace Hopper', 'grace@example.com'),
        'recipients': [(1, 'Alan Turing', 'alan@example.org')],
        'text': 'Hello from RTF & HTML.\r\n\r\nSecond paragraph.',
        'rtf': RTF, 'rtf_html': RTF_HTML,
    },
    'text': {
        'name': 'text', 'subject': 'Caf\u00e9 menu', 'ansi': True,
        'from': ('Ren\u00e9 Descartes', 'rene@example.fr'),
        'recipients': [(1, 'Blaise Pascal', 'blaise@example.fr')],
        'text': 'Plain text body with \u00fcmlauts.\r\nSecond line.',
    },
    'embedded': {
        'name': 'embedded', 'subject': 'Forwarding the inner message',
        'from': ('Hedy Lamarr', 'hedy@example.com'),
        'recipients': [(1, 'Katherine Johnson', 'katherine@example.com')],
        'html': '<html><body><p>See the attached message.</p></body></html>',
        'attachments': [{'message': {
            'name': 'inner', 'subject': 'Inner message',
            'from': ('Emmy Noether', 'emmy@example.de'),
            'recipients': [(1, 'Hedy Lamarr', 'hedy@example.com')],
            'text': 'The inner message body.',
            'attachments': [{'name': 'inner.txt', 'type': 'text/plain', 'data': b'Inside the inner message.\r\n'}],
        }}],
    },
}

expected = {}
for name, m in MESSAGES.items():
    cfb.write_cfb(name + '.msg', 3, message(m))
    mime = eml(m)
    for i, part in enumerate(mime.walk()):
        if part.is_multipart():
            part.set_boundary('=_eml_%d' % i)  # Fixed, so the files only change when the messages do.
    with open(name + '.eml', 'wb') as f:
        f.write(mime.as_bytes())
    msg = extract_msg.openMsg(name + '.msg')
    html = msg.htmlBody
    expected[name] = {
        'subject': msg.subject,
        'sender': msg.sender,
        'to': msg.to,
        'cc': msg.cc or None,
        'body': (msg.body or '').replace('\r\n', '\n').strip(),
        'html': html.decode('utf-8', 'replace') if isinstance(html, bytes) else html,
        'attachments': sorted(a.getFilename() or a.longFilename or a.displayName or '' for a in msg.attachments),
    }
    msg.close()
with open('expected.json', 'w') as f:
    json.dump(expected, f, indent=2, sort_keys=True, ensure_ascii=True)
print(json.dumps({k: (v['subject'], v['attachments']) for k, v in expected.items()}, ensure_ascii=True))
