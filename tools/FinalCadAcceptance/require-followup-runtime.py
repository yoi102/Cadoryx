"""Require actual final87 provider and both recovery terminal audits."""
from pathlib import Path
import hashlib
import json
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
iteration = int(sys.argv[1])
assert 1 <= iteration <= 100
stage = ROOT / f'artifacts/af-final87-{iteration}'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
for name in ('audit-full.py', 'audit-provider.py', 'audit-hosts.py'):
    subprocess.run([sys.executable, str(Path(__file__).with_name(name)), str(iteration)], check=True)
freeze = stage / 'freeze.json'
records = []
for label, expected in (('recovery24', 24), ('main-status36', 36)):
    path = stage / label / 'runtime-1-verification.json'
    record = read(path)
    assert record['state'].startswith('PASS_FINAL87_CURRENT_')
    build = record['semanticBuild'] if label == 'recovery24' else record['application']
    assert sha(Path(build['path'])) == build['sha256'] == sha(freeze)
    assert len(record['cases']) == expected and not record['productionApplied'] and not record['desktopInputStarted']
    for key in ('fixture', 'fullBothNondeferred', 'terminal', 'result', 'binding'):
        assert sha(Path(record[key]['path'])) == record[key]['sha256']
    if label == 'recovery24':
        assert len(record['images']) == 52 and len(record['saves']) == 48
        for row in record['images'] + record['saves']:
            assert sha(Path(row['path'])) == row['sha256']
    else:
        assert record['passed'] == 36 and record['failed'] == 0 and len(record['frames']) == 144
        for row in record['frames']:
            assert sha(Path(row['image']['path'])) == row['image']['sha256']
    records.append(dict(label=label, terminalAudited=True))
print(json.dumps(dict(recovery=records, directImageReview='SEPARATE', windowOrPerformanceAccepted=False)))
