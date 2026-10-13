from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import shutil

ROOT = Path(__file__).resolve().parents[3]
TOOL = Path(__file__).resolve().parent
DRAFT = ROOT / 'artifacts/d7-analytic-branch-draft-3'
PARENT = ROOT / 'artifacts/d7-analytic-branch-draft-1'

def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()
def ref(p): return {'path': p.relative_to(ROOT).as_posix(), 'sha256': sha(p)}
def payload(folder):
    return [{'path': p.relative_to(folder).as_posix(), 'sha256': sha(p)}
            for p in sorted(folder.rglob('*')) if p.is_file()]

old = read(PARENT / 'freeze.json')
audit = read(PARENT / 'focused-verification.json')
assert not audit['candidateFocusedPassed']
for row in old['toolSource']:
    assert sha(ROOT / row['path']) == row['sha256']
assert [c['failed'] for c in audit['configurations']] == [111, 111, 75, 75]
rows = []
for config in old['configurations']:
    name = config['configuration']
    build_path = DRAFT / ('build-' + name.lower() + '-3-exit.json')
    build = read(build_path)
    assert build['exitCode'] == 0
    log = (ROOT / build['log']).read_text(encoding='utf-8-sig')
    assert '0 Warning(s)' in log and '0 Error(s)' in log
    origin = Path(config['testDirectory'])
    for row in config['payload']:
        assert sha(origin / row['path']) == row['sha256']
    stage = DRAFT / ('tests-' + name.lower() + '-1')
    assert not stage.exists(), 'Preserve revision 2 payload.'
    shutil.copytree(origin, stage)
    kernel = Path(build['output'])
    for suffix in ['.dll', '.pdb']:
        shutil.copyfile(kernel / ('Cadoryx.Kernel.Occt' + suffix), stage / ('Cadoryx.Kernel.Occt' + suffix))
    changed = {r['path'] for r in config['payload'] if sha(stage / r['path']) != r['sha256']}
    assert changed == {'Cadoryx.Kernel.Occt.dll', 'Cadoryx.Kernel.Occt.pdb'}
    # The original discovered test assembly, every companion and native DLL
    # remain byte-identical. The existing baseline applies to these same tests.
    assert sha(stage / 'Cadoryx.Tests.dll') == sha(origin / 'Cadoryx.Tests.dll')
    rows.append({'configuration': name, 'build': ref(build_path), 'testDirectory': str(stage),
                 'payload': payload(stage), 'discovered6974': config['discovered6974'],
                 'added144': config['added144'], 'originalTestAssemblyByteIdentical': True,
                 'unchangedBaselineAudit': ref(PARENT / 'focused-verification.json')})
record = {'recordedAt': datetime.now(timezone.utc).isoformat(),
          'state': 'D7_REVISION3_BOUNDARY_ONLY_BOTH_BUILT_SAME_TEST_ASSEMBLIES_NOT_RUNTIME',
          'parent': ref(PARENT / 'freeze.json'), 'baselineEvidence': ref(PARENT / 'focused-verification.json'),
          'configurations': rows, 'native62': old['native62'], 'focusedFilter': old['focusedFilter'],
          'toolSource': [ref(p) for p in sorted(TOOL.iterdir()) if p.is_file() and p.suffix in ['.cs', '.csproj', '.props', '.py']],
          'unchangedTestCount': 6974, 'focusCount': 243, 'original99': 99, 'added144': 144,
          'adaptiveMeasurementEpsilon': 1e-11, 'geometryBudgetsChanged': False,
          'completeStartHelperWired': False, 'newPlanarWireFaceAPIReceived': False,
          'productionApplied': False, 'originalRawFar90FailuresChanged': False,
          'originalStrictSTEP24Changed': False, 'AFComplete': False}
target = DRAFT / 'freeze.json'
assert not target.exists()
target.write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'configurations': 2, 'sameActualTestAssembly': True, 'original6974': True,
                  'kernelOnlyReplaced': True, 'focusedRuntimeExecuted': False}))
