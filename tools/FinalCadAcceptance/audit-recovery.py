"""Run the preserved recovery assertions with verified final87 provenance."""
from pathlib import Path
import hashlib
import json
import os
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
iteration, label = int(sys.argv[1]), sys.argv[2]
runtime = int(sys.argv[3]) if len(sys.argv) == 4 else 1
assert 1 <= iteration <= 100 and 1 <= runtime <= 100 and label in ('recovery24', 'main-status36')
stage = ROOT / f'artifacts/af-final87-{iteration}'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
record = read(stage / 'recovery-auditors-preparation.json')
assert record['iteration'] == iteration and not record['runtimeStarted']
for row in record['projections']:
    assert row['assertionsUnchanged']
    for key in ('original', 'projected'):
        assert sha(ROOT / row[key]['path']) == row[key]['sha256']
for key in ('adapterSource', 'adapter'):
    assert sha(ROOT / record[key]['path']) == record[key]['sha256']
name = 'audit-recovery24.py' if label == 'recovery24' else 'audit-main-status36.py'
environment = dict(os.environ, CADORYX_FINAL_ACCEPTANCE_ITERATION=str(iteration))
subprocess.run([sys.executable, str(stage / 'recovery-auditors' / name), str(runtime)], env=environment, check=True)
