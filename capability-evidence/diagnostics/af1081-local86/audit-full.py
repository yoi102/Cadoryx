from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent
DRAFT = ROOT / 'artifacts/af1081-local86-1'
def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()
def ref(p): return {'path': p.relative_to(ROOT).as_posix(), 'sha256': sha(p)}
freeze = read(DRAFT / 'freeze.json')
single = sys.argv[1] if len(sys.argv) == 2 else None
if single is not None:
    assert single in ['Release', 'Debug']
    suite_path = DRAFT / f'full6830-{single.lower()}-1/exit.json'
    suite = {'configurations': [read(suite_path)]}
else:
    suite_path = DRAFT / 'full-both-exit.json'
    suite = read(suite_path)
parent = read(ROOT / 'capability-evidence/diagnostics/af1076-root-full/verification.json')
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
expected_native = {r['name']: r['sha256'] for r in freeze['native62']}
for row in freeze['sourcePayload']: assert sha(Path(freeze['workspace']) / row['path']) == row['sha256']
checks = []
namesets = []
for gate in suite['configurations']:
    config = gate['configuration']
    folder = Path(gate['output'])
    terminal = read(folder / 'exit.json')
    frozen = next(c for c in freeze['configurations'] if c['configuration'] == config)
    binary = Path(frozen['testDirectory'])
    for row in frozen['payload']: assert sha(binary / row['path']) == row['sha256']
    tree = ET.parse(folder / 'runtime.trx')
    counts = tree.find('.//t:Counters', ns).attrib
    results = tree.findall('.//t:UnitTestResult', ns)
    names = {r.attrib['testName'] for r in results}
    assert len(results) == len(names) == int(counts['total']) == 6830
    assert names == set(frozen['discoveredNames'])
    old = next(c for c in parent['configurations'] if c['configuration'] == config)
    old_trx = Path(old['trx']['path'])
    if not old_trx.is_absolute(): old_trx = ROOT / old_trx
    assert sha(old_trx) == old['trx']['sha256']
    old_results = ET.parse(old_trx).findall('.//t:UnitTestResult', ns)
    old_names = {r.attrib['testName'] for r in old_results}
    assert len(old_names) == 6652 and old_names <= names
    old_failures = {r.attrib['testName'] for r in old_results if r.attrib['outcome'] == 'Failed'}
    failures = [{'name': r.attrib['testName'], 'error': r.find('.//t:Message', ns).text}
                for r in results if r.attrib['outcome'] == 'Failed']
    failed_names = {r['name'] for r in failures}
    assert terminal['exitCode'] == gate['exitCode'] == (1 if failures else 0)
    assert int(counts['passed']) + int(counts['failed']) + int(counts['notExecuted']) == 6830
    binding = read(folder / 'loaded-native.json')
    assert len(binding['native']) == 62
    assert {r['name'] for r in binding['native']} == expected_native.keys()
    for row in binding['native']:
        assert Path(row['path']).parent == binary / 'occt'
        assert sha(Path(row['path'])) == row['sha256'] == expected_native[row['name']]
    checks.append({'configuration': config, 'total': 6830, 'passed': int(counts['passed']),
                   'failed': int(counts['failed']), 'skipped': int(counts['notExecuted']),
                   'parent6652NamesRetained': True, 'added178Names': sorted(names - old_names),
                   'failures': failures, 'newFailureNames': sorted(failed_names - old_failures),
                   'originalFailuresNowPassed': sorted(old_failures - failed_names),
                   'actualNative62Matched': True, 'trx': ref(folder / 'runtime.trx'),
                   'binding': ref(folder / 'loaded-native.json'), 'exit': ref(folder / 'exit.json')})
    namesets.append(names)
assert len(checks) == (1 if single else 2)
if not single: assert namesets[0] == namesets[1]
result = {'recordedAt': datetime.now(timezone.utc).isoformat(),
          'state': f'LOCAL86_SOURCE1081_{single.upper()}_FULL6830_ACTUALLY_EXECUTED_AND_VERIFIED' if single else 'LOCAL86_SOURCE1081_FULL6830_BOTH_ACTUALLY_EXECUTED_AND_VERIFIED',
          'freeze': ref(DRAFT / 'freeze.json'), 'suite': ref(suite_path),
          'baseHead': freeze['baseHead'], 'configurations': checks,
          'allPassed': all(c['failed'] == c['skipped'] == 0 for c in checks),
          'originalStrictSTEPGateChanged': False, 'productionApplied': False,
          'publiclyPublished': False, 'desktopOrModelGateProven': False, 'AFComplete': False,
          'G': 'USER_DEFERRED', 'cleanup': False}
target = OUT / (f'{single.lower()}-full-verification.json' if single else 'full-verification.json')
assert not target.exists()
target.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'configs': [{k: c[k] for k in ['configuration', 'passed', 'failed', 'skipped']} for c in checks]}))
