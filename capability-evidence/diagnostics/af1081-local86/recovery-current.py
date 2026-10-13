"""Read the actual frozen candidate and completed full gate for recovery audits."""
from pathlib import Path
import hashlib
import json

ROOT = Path(__file__).resolve().parents[3]
a = ROOT / 'artifacts'
DRAFT = a / 'af1081-local86-1'
def read(path): return json.loads(path.read_text(encoding='utf-8-sig'))
def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest().upper()
def ref(path): return {'path': str(path.resolve()), 'sha256': sha(path)}

def accepted_app(label):
    freeze_path = DRAFT / 'freeze.json'
    freeze = read(freeze_path)
    full_path = ROOT / 'capability-evidence/diagnostics/af1081-local86/full-verification.json'
    full = read(full_path)
    assert len(full['configurations']) == 2 and not full['originalStrictSTEPGateChanged']
    for c in full['configurations']:
        assert c['total'] == 6830 and c['failed'] == 24 and c['skipped'] == 0
        assert not c['newFailureNames'] and c['actualNative62Matched']
        for key in ['trx', 'binding', 'exit']:
            p = ROOT / c[key]['path']
            assert sha(p) == c[key]['sha256']
    for r in freeze['sourcePayload']: assert sha(Path(freeze['workspace']) / r['path']) == r['sha256']
    app_config = next(c for c in freeze['configurations'] if c['configuration'] == 'Release')
    app_path = Path(app_config['candidate'])
    for r in app_config['appPayload']: assert sha(app_path / r['path']) == r['sha256']
    fixture_path = ROOT / 'capability-evidence/diagnostics/af1081-local86/recovery-build-verification.json'
    build = read(fixture_path)
    assert build['state'] == 'ORIGINAL_RECOVERY24_MAIN_STATUS36_BOTH_BUILD0_CURRENT1081_LOCAL86_NOT_RUNTIME'
    assert sha(freeze_path) == build['freeze']['sha256']
    prep_path = DRAFT / 'recovery-preparation.json'
    prep = read(prep_path)
    assert sha(prep_path) == build['preparation']['sha256']
    for r in prep['sources']:
        for key in ['source', 'candidate']: assert sha(ROOT / r[key]['path']) == r[key]['sha256']
        if r['csharpByteIdentical'] is not None: assert r['csharpByteIdentical']
    stage = next(c for c in build['configurations'] if c['label'] == label and c['configuration'] == 'Release')
    output = Path(stage['output'])
    assert stage['exitCode'] == 0 and Path(stage['candidate']) == app_path
    for r in stage['payload']: assert sha(output / r['path']) == r['sha256']
    # Adapt already-verified build metadata for the unchanged original runtime
    # validation bodies. These dictionaries are not runtime results or receipts.
    app = {'candidate': str(app_path), 'native': freeze['native62'], 'build': ref(freeze_path)}
    fixture = {'candidate': str(output), 'payload': stage['payload'],
               'compiledAgainst': ref(freeze_path), 'matrix': prep['original36Matrix']}
    return app, ref(full_path), fixture, fixture_path, prep_path
