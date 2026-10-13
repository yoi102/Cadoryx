from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[3]
DRAFT = ROOT / 'artifacts/d7-analytic-branch-draft-4'
def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()
def ref(p): return {'path': p.relative_to(ROOT).as_posix(), 'sha256': sha(p)}
freeze_path = DRAFT / 'freeze.json'
freeze = read(freeze_path)
suite_path = DRAFT / 'focused-both-exit.json'
suite = read(suite_path)
assert len(suite['configurations']) == 2
for row in freeze['toolSource']:
    assert sha(ROOT / row['path']) == row['sha256']
baseline = ROOT / freeze['baselineEvidence']['path']
assert sha(baseline) == freeze['baselineEvidence']['sha256']
native = {r['name']: r['sha256'] for r in freeze['native62']}
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
checks = []
for run in suite['configurations']:
    config = next(c for c in freeze['configurations'] if c['configuration'] == run['configuration'])
    assert run['mode'] == 'candidate'
    binary = Path(config['testDirectory'])
    for row in config['payload']:
        assert sha(binary / row['path']) == row['sha256']
    folder = Path(run['output'])
    tree = ET.parse(folder / 'runtime.trx')
    rows = tree.findall('.//t:UnitTestResult', ns)
    counts = tree.find('.//t:Counters', ns).attrib
    names = {r.attrib['testName'] for r in rows}
    old = {n for n in config['discovered6974'] if n.startswith((
        'Cadoryx.Tests.FiniteSurfaceExtrusionTests.', 'Cadoryx.Tests.FiniteSurfaceNewBodyTests.'))}
    assert len(old) == 99 and len(config['added144']) == 144
    assert names == set(config['added144']) | old
    assert len(rows) == len(names) == int(counts['total']) == 243
    assert int(counts['notExecuted']) == 0
    failed = [{'name': r.attrib['testName'], 'message': r.find('.//t:Message', ns).text}
              for r in rows if r.attrib['outcome'] == 'Failed']
    assert run['exitCode'] == (1 if failed else 0)
    binding = read(folder / 'loaded-native.json')
    assert len(binding['native']) == 62
    assert {r['name'] for r in binding['native']} == set(native)
    for row in binding['native']:
        assert Path(row['path']).parent == binary / 'occt'
        assert sha(Path(row['path'])) == row['sha256'] == native[row['name']]
    checks.append({'configuration': run['configuration'], 'total': 243,
                   'passed': int(counts['passed']), 'failed': len(failed), 'skipped': 0,
                   'failures': failed, 'old99Failures': [f for f in failed if f['name'] in old],
                   'actualNative62Matched': True, 'trx': ref(folder / 'runtime.trx'),
                   'binding': ref(folder / 'loaded-native.json'), 'terminal': ref(folder / 'exit.json')})
record = {'recordedAt': datetime.now(timezone.utc).isoformat(),
          'state': 'D7_REVISION4_COMPLETE_NATIVE_WIRE_MATERIAL_BOTH_ACTUAL243_INDEPENDENTLY_AUDITED',
          'freeze': ref(freeze_path), 'sdkReceivedGates': freeze['sdkReceivedGates'], 'suite': ref(suite_path), 'unchangedBaseline': ref(baseline),
          'configurations': checks, 'candidateFocusedPassed': all(c['failed'] == 0 for c in checks),
          'whole6974RegressionExecuted': False, 'geometryBudgetsChanged': False,
          'originalRaw90FailuresChanged': False, 'originalStrictSTEP24Changed': False,
          'completeStartHelperWired': True, 'newPlanarWireFaceAPIReceived': True,
          'productionApplied': False, 'actualWindowExecuted': False, 'AFComplete': False}
target = DRAFT / 'focused-verification.json'
assert not target.exists()
target.write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'configurations': [{k: c[k] for k in ['configuration','total','passed','failed']} for c in checks],
                  'candidateFocusedPassed': record['candidateFocusedPassed'], 'productionApplied': False}))
