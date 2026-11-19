"""Records in expected.json what olefile and extract-msg (independent readers) find in the Apache POI sample files of
this folder: every stream's path, size and SHA-256, the summary information, and for the .msg files the message's
subject, sender, recipients and attachments. Run with a venv that has olefile and extract-msg; from this folder:
python make-expected.py"""
import glob
import hashlib
import json

import extract_msg
import olefile

expected = {}
for name in sorted(glob.glob('*.msg') + glob.glob('*.doc') + glob.glob('*.xls') + glob.glob('*.ppt')):
    ole = olefile.OleFileIO(name)
    streams = {}
    for entry in ole.listdir(streams=True, storages=False):
        data = ole.openstream(entry).read()
        streams['/'.join(entry)] = {'size': len(data), 'sha256': hashlib.sha256(data).hexdigest()}
    meta = ole.get_metadata()
    file = {
        'version': ole.get_version()[0] if hasattr(ole, 'get_version') else None,
        'streams': streams,
        'title': meta.title.decode('cp1252', 'replace') if meta.title else None,
        'author': meta.author.decode('cp1252', 'replace') if meta.author else None,
    }
    ole.close()
    if name.endswith('.msg'):
        msg = extract_msg.openMsg(name)
        file['message'] = {
            'subject': msg.subject,
            'sender': msg.sender,
            'to': msg.to,
            'body': (msg.body or '').replace('\r\n', '\n').strip()[:200],
            'has_html': bool(msg.htmlBody),
            'attachments': sorted((a.longFilename or a.shortFilename or a.displayName or '') for a in msg.attachments),
        }
        msg.close()
    expected[name] = file
with open('expected.json', 'w') as f:
    json.dump(expected, f, indent=2, sort_keys=True, ensure_ascii=True)
print({k: len(v['streams']) for k, v in expected.items()})
