"""Capture the already applied, reviewed file list; never modifies upstream."""
from pathlib import Path
import base64
import difflib
import hashlib
import json
import os
import subprocess

here = Path(__file__).resolve().parent
upstream = Path(r'C:\Users\yoiri\source\repos\OcctSharp')
records = json.loads((here / 'changes.json').read_text(encoding='utf-8'))
env = dict(os.environ, GIT_CONFIG_GLOBAL='NUL', GIT_CONFIG_NOSYSTEM='1')
patch = []
for record in records:
    path = record['path']
    if '..' in Path(path).parts or any(p.lower() in ('generated', '.git') for p in Path(path).parts):
        raise ValueError('Invalid upstream file boundary')
    data = (upstream / path).read_bytes()
    old = b''
    if record['before']:
        old = subprocess.check_output(['git', '-c', f'safe.directory={upstream.as_posix()}', '-C', str(upstream),
                                       'show', f'HEAD:{path}'], env=env)
    record['after'] = hashlib.sha256(data).hexdigest()
    record['content'] = base64.b64encode(data).decode('ascii')
    patch.extend(difflib.unified_diff(old.decode('utf-8-sig').replace('\r\n', '\n').splitlines(keepends=True),
        data.decode('utf-8-sig').replace('\r\n', '\n').splitlines(keepends=True),
        fromfile='a/'+path if record['before'] else '/dev/null', tofile='b/'+path))
(here / 'changes.json').write_text(json.dumps(records, indent=2), encoding='utf-8')
(here / 'upstream.patch').write_text(''.join(patch), encoding='utf-8')
print(f'Captured {len(records)} upstream files; original hash preconditions retained.')
