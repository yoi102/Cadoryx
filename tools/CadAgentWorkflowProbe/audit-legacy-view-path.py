"""Verify the original rejection, added feedback and existing native view creation."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
DRAFT = ROOT / 'artifacts/af1082-drawing-legacy-path-1'
def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()
def ref(p): return {'path': p.relative_to(ROOT).as_posix(), 'sha256': sha(p)}
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def trx(p):
    tree = ET.parse(p)
    rows = tree.findall('.//t:UnitTestResult', ns)
    counts = tree.find('.//t:Counters', ns).attrib
    names = {r.attrib['testName'] for r in rows}
    assert len(rows) == len(names) == int(counts['total'])
    assert int(counts['notExecuted']) == 0
    return rows, names, counts

original = read(ROOT / 'artifacts/af1081-local86-1/freeze.json')
old_names = set(original['configurations'][0]['discoveredNames'])
public_path = ROOT / 'capability-evidence/diagnostics/af1080-final-ready/freeze.json'
public = read(public_path)
native = {r['name']: r['sha256'] for r in public['native62']}
checks = []
new_sets = []
for config in ['Release', 'Debug']:
    baseline = DRAFT / ('baseline-' + config.lower())
    candidate = DRAFT / ('candidate-' + config.lower())
    before_rows, added, before_counts = trx(baseline / 'tests.trx')
    rows, names, counts = trx(candidate / 'tests.trx')
    assert len(added) == 17 and all(n.startswith('Cadoryx.Tests.DrawingLegacyViewPathFeedbackTests.') for n in added)
    assert int(before_counts['passed']) == 7 and int(before_counts['failed']) == 10
    assert all('.InvalidLegacyViewPathOffers' in r.attrib['testName'] for r in before_rows if r.attrib['outcome'] == 'Failed')
    assert len(names) == 49 and int(counts['passed']) == 49 and int(counts['failed']) == 0
    assert added <= names and names - added <= old_names and len(names - added) == 32
    assert 'Cadoryx.Tests.CadAgentDocumentToolTests.AgentDrawingViewUsesExplicitSheetAndInstancePath' in names
    assert read(baseline / 'exit.json')['exitCode'] == 1
    end = read(candidate / 'exit.json')
    assert end['exitCode'] == 0 and end['nativeBindingCaptured']
    assert end['productionSourceSha256'] == sha(ROOT / 'Cadoryx.ViewModels/Toolboxes/CadAgentDocumentTools.cs')
    binary = ROOT / 'Cadoryx.Tests/bin' / config / 'net10.0'
    assert end['testSha256'] == sha(binary / 'Cadoryx.Tests.dll')
    assert end['viewModelsSha256'] == sha(binary / 'Cadoryx.ViewModels.dll')
    binding = read(candidate / 'loaded-native.json')
    assert len(binding['native']) == 62 and {r['name'] for r in binding['native']} == set(native)
    for row in binding['native']:
        assert Path(row['path']).parent == binary / 'occt'
        assert sha(Path(row['path'])) == row['sha256'] == native[row['name']]
    builds = []
    for phase in ['baseline', 'candidate']:
        exit_path = DRAFT / (phase + '-build-' + config.lower() + '-exit.json')
        build = read(exit_path)
        assert build['exitCode'] == 0
        log = (ROOT / build['log']).read_text(encoding='utf-8-sig')
        assert '0 Warning(s)' in log and '0 Error(s)' in log
        builds.append(ref(exit_path))
    checks.append({'configuration': config, 'baselineNew17Passed': 7, 'baselineNew17Failed': 10,
                   'candidate49Passed': 49, 'candidateFailed': 0, 'skipped': 0,
                   'original32Preserved': True, 'existingSuccessfulNativeViewTestPassed': True,
                   'actualPublic85Native62Matched': True, 'builds': builds,
                   'baseline': ref(baseline / 'tests.trx'), 'candidate': ref(candidate / 'tests.trx'),
                   'binding': ref(candidate / 'loaded-native.json'), 'terminal': ref(candidate / 'exit.json')})
    new_sets.append(added)
assert new_sets[0] == new_sets[1]
record = {'recordedAt': datetime.now(timezone.utc).isoformat(),
          'state': 'AF1082_LEGACY_DRAWING_VIEW_PATH_GUIDANCE_BOTH_FOCUSED49_PASSED',
          'source': ref(ROOT / 'Cadoryx.ViewModels/Toolboxes/CadAgentDocumentTools.cs'),
          'tests': ref(ROOT / 'Cadoryx.Tests/DrawingLegacyViewPathFeedbackTests.cs'),
          'baselineSource': ref(DRAFT / 'baseline-CadAgentDocumentTools.cs'),
          'publicSDKNativeBaseline': ref(public_path), 'configurations': checks,
          'defaultToolSchemasChanged': False, 'legacyValidViewEntryPreserved': True,
          'documentStateSheetAndLifecycleGuardsPreserved': True, 'identitiesInferredOrFilled': False,
          'rejectedRequestRetriedAutomatically': False, 'originalStrictSTEP24Changed': False,
          'modelAcceptanceRerun': False, 'current6847FullRegressionExecuted': False,
          'productionSourceApplied': True, 'local86Applied': False, 'AFComplete': False,
          'G': 'USER_DEFERRED', 'cleanup': False}
target = ROOT / 'capability-evidence/diagnostics/af1082-drawing-legacy-path/verification.json'
assert not target.exists()
target.parent.mkdir(parents=True, exist_ok=True)
target.write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'configurations': 2, 'baselineNew17': '7 passed/10 failed', 'actualFocused49': '49 passed/0 failed',
                  'actualNative62Public85Matched': True, 'realModelRerun': False}))
