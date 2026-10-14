"""Map actual final87 build evidence to the unchanged recovery assertions."""
from pathlib import Path
import hashlib
import json
import os
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
iteration = int(os.environ['CADORYX_FINAL_ACCEPTANCE_ITERATION'])
assert 1 <= iteration <= 100
DRAFT = ROOT / f'artifacts/af-final87-{iteration}'
a = ROOT / 'artifacts'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref = lambda p: dict(path=str(p.resolve()), sha256=sha(p))

def accepted_app(label, ui_culture_revision=False):
    assert label in ('recovery24', 'main-status36') and not ui_culture_revision
    subprocess.run([sys.executable, str(ROOT / 'tools/FinalCadAcceptance/audit-full.py'), str(iteration)], check=True)
    subprocess.run([sys.executable, str(ROOT / 'tools/FinalCadAcceptance/audit-hosts.py'), str(iteration)], check=True)
    freeze_path = DRAFT / 'freeze.json'
    freeze = read(freeze_path)
    full_path = ROOT / f'capability-evidence/diagnostics/af-final87/candidate{iteration}-full-verification.json'
    full = read(full_path)
    assert len(full['configurations']) == 2 and full['exactSTEP24NotExecuted']
    assert freeze['expectedExecuted'] == freeze['expectedTotal'] - 24
    assert all(c['passed'] == freeze['expectedExecuted'] and c['failed'] == c['skipped'] == 0 for c in full['configurations'])
    for row in freeze['candidateSource']:
        assert sha(Path(freeze['workspace']) / row['path']) == row['sha256']
    config = next(c for c in freeze['configurations'] if c['configuration'] == 'Release')
    app_path = Path(config['candidate'])
    for row in config['appPayload']:
        assert sha(app_path / row['path']) == row['sha256']
    fixture_path = DRAFT / 'hosts-verification.json'
    hosts = read(fixture_path)
    assert hosts['freeze']['sha256'] == sha(freeze_path)
    stage = next(c for c in hosts['hosts'] if c['label'] == label and c['configuration'] == 'Release')
    output = Path(stage['output'])
    assert stage['exitCode'] == 0 and Path(stage['candidate']) == app_path
    for row in stage['payload']:
        assert sha(output / row['path']) == row['sha256']
    prep_path = DRAFT / 'followup-preparation.json'
    prep = read(prep_path)
    assert prep['freeze']['sha256'] == sha(freeze_path)
    assert prep['recovery']['original24'] == 24 and prep['recovery']['original36Matrix']['planned'] == 36
    app = dict(candidate=str(app_path), native=freeze['native62'], build=ref(freeze_path))
    fixture = dict(candidate=str(output), payload=stage['payload'], compiledAgainst=ref(freeze_path),
                   matrix=prep['recovery']['original36Matrix'])
    return app, ref(full_path), fixture, fixture_path, prep_path
