"""Bind the navigation fix to original regressions and actual final87 runtimes."""
from pathlib import Path
from collections import Counter
from datetime import datetime, timezone
import hashlib, json, subprocess, sys, xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
iteration = int(sys.argv[1])
assert 1 <= iteration <= 100
STAGE = ROOT / f'artifacts/af-final87-{iteration}'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref = lambda p: dict(path=p.relative_to(ROOT).as_posix(), sha256=sha(p))
def results(path):
    tree = ET.parse(path).getroot()
    return tree.findall('.//t:UnitTestResult', {'t': tree.tag.split('}')[0].strip('{')})

freeze = read(STAGE / 'freeze.json')
suite = read(STAGE / 'focused-both-exit.json')
assert suite['focused'] and len(suite['configurations']) == 2
classes = {'NativeOmittedGeometryReadTests', 'NativeCompleteSourceContextTests',
           'AgentNativeSelectionRetentionTests', 'LmStudioCompletionIntegrityTests',
           'AgentEmptyResponseRecoveryTests'}
if iteration >= 4:
    classes.update({'AgentOpaqueIdentityBudgetTests','AgentContextTests','DrawingContractRequestGuideTests','DrawingAgentTests'})
if iteration >= 6:
    classes.update({'DrawingInspectionTypeFeedbackTests','ExchangeOccurrenceSemanticTests','ExchangeMeasurementTests'})
old_trx = ROOT / 'artifacts/af-final87-1/full-release-1/runtime.trx'
old = results(old_trx)
assert len(old) == 7168 and Counter(r.attrib['outcome'] for r in old) == {'Passed': 7164, 'Failed': 4}
failures = Counter(r.attrib['testName'] for r in old if r.attrib['outcome'] == 'Failed')
assert len(failures) == 4 and all(n.startswith('Cadoryx.Tests.NativeOmittedGeometryReadTests.') for n in failures)
native = {r['name']: r['sha256'] for r in freeze['native62']}
cases = []
for config in freeze['configurations']:
    folder = STAGE / f"focused-{config['configuration'].lower()}-1"
    terminal = read(folder / 'exit.json')
    assert terminal['exitCode'] == 0 and terminal['actualNativeBinding'] and not terminal['STEP24Retried']
    for row in config['payload']:
        assert sha(Path(config['testDirectory']) / row['path']) == row['sha256']
    expected = Counter(n for n in config['discoveredNames'] if n.split('.')[2] in classes)
    rows = results(folder / 'runtime.trx')
    assert len(rows) == sum(expected.values()) == suite['executedEach']
    assert all(r.attrib['outcome'] == 'Passed' for r in rows)
    assert len({r.attrib['testId'] for r in rows}) == len(rows)
    assert Counter(r.attrib['testName'] for r in rows) == expected and failures <= expected
    assert not (set(expected) & set(freeze['deferredNames']))
    binding = read(folder / 'loaded-native.json')
    assert len(binding['native']) == 62 and {r['name']: r['sha256'] for r in binding['native']} == native
    for row in binding['native']:
        assert Path(row['path']).parent == Path(config['testDirectory']) / 'occt'
        assert sha(Path(row['path'])) == row['sha256']
    cases.append(dict(configuration=config['configuration'], passed=len(rows), failed=0, skipped=0,
                      originalFourPassed=True, trx=ref(folder / 'runtime.trx'), native=ref(folder / 'loaded-native.json')))
original = 'Cadoryx.Tests/NativeOmittedGeometryReadTests.cs'
head_content = subprocess.check_output(['git', 'show', f'HEAD:{original}'], cwd=ROOT)
assert hashlib.sha256(head_content.replace(b'\r\n', b'\n')).hexdigest() == hashlib.sha256((ROOT / original).read_bytes().replace(b'\r\n', b'\n')).hexdigest()
record = dict(recordedAt=datetime.now(timezone.utc).isoformat(), state='ORIGINAL_FOUR_NAVIGATION_REGRESSIONS_FIXED_BOTH_ACTUAL62',
              freeze=ref(STAGE / 'freeze.json'), originalFailureTrx=ref(old_trx), originalFullPassed=7164,
              originalFullFailed=4, knownOriginalFailures=list(failures), configurations=cases,
              originalAssertionsAndBudgetsChanged=False, exactSTEP24NotRun=True,
              fullCurrentPassed=False, modelOrWindowAccepted=False, productionApplied=False, AFComplete=False)
out = ROOT / f'capability-evidence/diagnostics/af-final87/candidate{iteration}-focused-verification.json'
out.parent.mkdir(parents=True, exist_ok=True)
assert not out.exists()
out.write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
print(f"Both focused {suite['executedEach']} passed; original four fixed, original assertions unchanged, actual62 exact; full/model/window pending.")
