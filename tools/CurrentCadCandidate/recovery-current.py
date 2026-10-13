"""Adapt the verified current payload for unchanged recovery runtime assertions."""
from pathlib import Path
import hashlib
import json
import runpy

ROOT = Path(__file__).resolve().parents[2]
a = ROOT / 'artifacts'
DRAFT = a / 'af1082-f5-d7-local87cu1-1'
def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()
def ref(p): return {'path': str(p.resolve()), 'sha256': sha(p)}

def accepted_app(label):
    runpy.run_path(str(Path(__file__).with_name('require-full-gate.py')))
    freeze_path = DRAFT / 'freeze.json'; freeze = read(freeze_path)
    full_path = ROOT / 'capability-evidence/diagnostics/af1082-f5-d7-local87cu1/full-verification.json'
    full = read(full_path)
    assert len(full['configurations']) == 2 and full['noNewFailuresOutsideExactDeferral']
    assert not full['originalStrictSTEPGateChanged']
    deferred = read(ROOT / full['exactSTEP24Deferral']['path'])
    assert len(set(deferred['exactTestNames'])) == 24
    for config in full['configurations']:
        assert config['total'] == 7117 and config['skipped'] == 0
        assert not config['newFailureNames'] and config['actualNative62Matched']
        assert {row['name'] for row in config['failures']} <= set(deferred['exactTestNames'])
        for key in ['trx', 'binding', 'exit']:
            assert sha(ROOT / config[key]['path']) == config[key]['sha256']
    for row in freeze['sourcePayload']:
        assert sha(Path(freeze['workspace']) / row['path']) == row['sha256']
    app_config = next(c for c in freeze['configurations'] if c['configuration'] == 'Release')
    app_path = Path(app_config['candidate'])
    for row in app_config['appPayload']: assert sha(app_path / row['path']) == row['sha256']
    fixture_path = ROOT / 'capability-evidence/diagnostics/af1082-f5-d7-local87cu1/hosts-verification.json'
    build = read(fixture_path)
    assert build['state'] == 'CURRENT_F5_D7_BOTH_RECOVERY24_MAIN36_D6WINDOW36_HOSTS_PUBLISH0_NOT_RUNTIME'
    assert sha(freeze_path) == build['freeze']['sha256']
    original_sources = read(ROOT / build['originalRecoverySources']['path'])
    for row in original_sources['sources']:
        if row['path'] == 'tools/CadRecoveryWindowProbe/README.md':
            data = (ROOT / row['path']).read_bytes()
            old = b'`capability-evidence/diagnostics/af1081-local86/run-recovery.ps1`'
            new = b'`tools/CurrentCadCandidate/run-recovery.ps1`'
            assert data.count(new) == 1 and old not in data
            assert hashlib.sha256(data.replace(new, old)).hexdigest().upper() == row['sha256']
        else: assert sha(ROOT / row['path']) == row['sha256']
    prep_path = DRAFT / 'recovery-preparation.json'; prep = read(prep_path)
    assert prep['original24'] == 24 and prep['original36Matrix']['planned'] == 36
    stage = next(c for c in build['configurations'] if c['label'] == label and c['configuration'] == 'Release')
    output = Path(stage['output'])
    assert stage['exitCode'] == 0 and Path(stage['candidate']) == app_path
    for row in stage['payload']: assert sha(output / row['path']) == row['sha256']
    # Build metadata only; never synthetic runtime results.
    app = {'candidate': str(app_path), 'native': freeze['native62'], 'build': ref(freeze_path)}
    fixture = {'candidate': str(output), 'payload': stage['payload'],
               'compiledAgainst': ref(freeze_path), 'matrix': prep['original36Matrix']}
    return app, ref(full_path), fixture, fixture_path, prep_path
