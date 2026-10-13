"""Check terminal test evidence before a consumer stage; no native code runs."""
from pathlib import Path
import hashlib
import json
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
DRAFT = ROOT / 'artifacts/af1082-f5-d7-local87cu1-1'
def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
path = ROOT / 'capability-evidence/diagnostics/af1082-f5-d7-local87cu1/full-verification.json'
proof = read(path)
freeze_path = DRAFT / 'freeze.json'; freeze = read(freeze_path)
assert proof['freeze']['sha256'] == sha(freeze_path)
assert not proof['originalStrictSTEPGateChanged'] and proof['noNewFailuresOutsideExactDeferral']
deferred = read(ROOT / proof['exactSTEP24Deferral']['path'])
assert sha(ROOT / proof['exactSTEP24Deferral']['path']) == proof['exactSTEP24Deferral']['sha256']
allowed = set(deferred['exactTestNames']); assert len(allowed) == 24
assert len(proof['configurations']) == 2
assert {r['configuration'] for r in proof['configurations']} == {'Release', 'Debug'}
expected_native = {r['name']: r['sha256'] for r in freeze['native62']}
for config in proof['configurations']:
    frozen = next(c for c in freeze['configurations'] if c['configuration'] == config['configuration'])
    assert config['total'] == 7117 and config['skipped'] == 0 and not config['newFailureNames']
    for payload in frozen['payload']:
        assert sha(Path(frozen['testDirectory']) / payload['path']) == payload['sha256']
    for key in ['trx', 'binding', 'exit']:
        assert sha(ROOT / config[key]['path']) == config[key]['sha256']
    tree = ET.parse(ROOT / config['trx']['path'])
    rows = tree.findall('.//t:UnitTestResult', ns)
    counters = tree.find('.//t:Counters', ns).attrib
    names = {r.attrib['testName'] for r in rows}
    assert len(rows) == len(names) == 7117 and names == set(frozen['discoveredNames'])
    failed = {r.attrib['testName'] for r in rows if r.attrib['outcome'] == 'Failed'}
    assert failed <= allowed and failed == {r['name'] for r in config['failures']}
    assert len(failed) == config['failed'] and config['passed'] == 7117 - len(failed)
    assert int(counters['total']) == int(counters['executed']) == 7117
    assert int(counters['passed']) == config['passed']
    assert int(counters['failed']) == config['failed'] and int(counters['notExecuted']) == 0
    assert all(r.attrib['outcome'] in ['Passed', 'Failed'] for r in rows)
    terminal = read(ROOT / config['exit']['path'])
    assert terminal['exitCode'] == (1 if failed else 0)
    binding = read(ROOT / config['binding']['path'])
    assert len(binding['native']) == 62
    assert {r['name']: r['sha256'] for r in binding['native']} == expected_native
    for module in binding['native']:
        assert Path(module['path']).parent == Path(frozen['testDirectory']) / 'occt'
        assert sha(Path(module['path'])) == module['sha256']
for row in freeze['sourcePayload']:
    assert sha(Path(freeze['workspace']) / row['path']) == row['sha256']
print(json.dumps({'actualFullBoth7117Validated': True, 'onlyExactDeferredFailures': True,
                  'nativeConsumerStartedHere': False}))
