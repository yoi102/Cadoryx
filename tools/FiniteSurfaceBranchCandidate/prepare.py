from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import shutil

ROOT = Path(__file__).resolve().parents[2]
DRAFT = ROOT / 'artifacts/d7-analytic-branch-draft-1'
PARENT = ROOT / 'artifacts/af1081-local86-1'
TOOL = Path(__file__).resolve().parent

def read(path): return json.loads(path.read_text(encoding='utf-8-sig'))
def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest().upper()
def ref(path): return {'path': path.relative_to(ROOT).as_posix(), 'sha256': sha(path)}
def payload(folder):
    return [{'path': p.relative_to(folder).as_posix(), 'sha256': sha(p)}
            for p in sorted(folder.rglob('*')) if p.is_file()]

freeze = read(PARENT / 'freeze.json')
for row in freeze['sourcePayload']:
    assert sha(Path(freeze['workspace']) / row['path']) == row['sha256']
original_kernel = Path(freeze['configurations'][0]['testDirectory']) / 'Cadoryx.Kernel.Occt.dll'
rows = []
case_sets = []
for config in ['Release', 'Debug']:
    build_path = DRAFT / f'build-{config.lower()}-6-exit.json'
    build = read(build_path)
    assert build['exitCode'] == 0 and build['originalNet10TestFramework']
    assert sha(ROOT / build['overlay']) == build['overlaySha256']
    assert sha(TOOL / 'OcctFiniteSurfaceBranchDomain.cs') == build['helperSha256']
    assert sha(TOOL / 'FiniteSurfaceAnalyticBranchTests.cs') == build['testSourceSha256']
    log = Path(build['log']).read_text(encoding='utf-8-sig')
    assert '0 Warning(s)' in log and '0 Error(s)' in log
    folder = Path(build['output'])
    original_tests = Path(build['originalTests'])
    for row in freeze['managed13']:
        assert sha(folder / row['name']) == row['sha256']
    assert {p.name for p in (folder / 'occt').glob('*.dll')} == {r['name'] for r in freeze['native62']}
    for row in freeze['native62']:
        assert sha(folder / 'occt' / row['name']) == row['sha256']
    assert sha(folder / 'Cadoryx.Kernel.Occt.dll') != sha(original_kernel)
    for p in folder.glob('*.dll'):
        if p.name in ['Cadoryx.Tests.dll', 'Cadoryx.Kernel.Occt.dll']:
            continue
        assert sha(p) == sha(original_tests / p.name), p.name
    runtime = read(folder / 'Cadoryx.Tests.runtimeconfig.json')['runtimeOptions']
    assert runtime['tfm'] == 'net10.0' and runtime['framework']['name'] == 'Microsoft.NETCore.App'
    discovery_path = DRAFT / f'discovery-{config.lower()}-6.log'
    lines = discovery_path.read_text(encoding='utf-8-sig').splitlines()
    names = {s.strip() for s in lines if s.startswith('    Cadoryx.Tests.')}
    parent_names = set(next(c for c in freeze['configurations'] if c['configuration'] == config)['discoveredNames'])
    assert len(parent_names) == 6830 and parent_names <= names
    added = names - parent_names
    assert len(added) == 144 and all(n.startswith('Cadoryx.Tests.FiniteSurfaceAnalyticBranchTests.') for n in added)
    assert sum('.ExplicitBranchRetains' in n for n in added) == 108
    assert sum('.MissingSelectedHalf' in n for n in added) == 36
    assert len(names) == 6974
    case_sets.append(names)
    baseline = DRAFT / f'baseline-{config.lower()}-1'
    assert not baseline.exists(), 'Preserve the previous baseline.'
    shutil.copytree(folder, baseline)
    shutil.copyfile(original_kernel, baseline / 'Cadoryx.Kernel.Occt.dll')
    original_pdb = original_kernel.with_suffix('.pdb')
    if original_pdb.exists(): shutil.copyfile(original_pdb, baseline / original_pdb.name)
    assert sha(baseline / 'Cadoryx.Kernel.Occt.dll') == sha(original_kernel)
    assert sha(baseline / 'Cadoryx.Tests.dll') == sha(folder / 'Cadoryx.Tests.dll')
    rows.append({'configuration': config, 'build': ref(build_path), 'discovery': ref(discovery_path),
                 'testDirectory': str(folder), 'payload': payload(folder),
                 'baselineDirectory': str(baseline), 'baselinePayload': payload(baseline),
                 'originalKernel': ref(original_kernel), 'original6830NamesRetained': True,
                 'discovered6974': sorted(names), 'added144': sorted(added),
                 'companions': 'Original frozen Release DLLs; candidate kernel/test compilation differs by configuration'})
assert case_sets[0] == case_sets[1]
sources = [ref(p) for p in sorted(TOOL.iterdir()) if p.is_file() and p.suffix in ['.cs', '.csproj', '.props', '.py']]
record = {'recordedAt': datetime.now(timezone.utc).isoformat(),
          'state': 'D7_ISOLATED_BOTH_BUILT_ACTUAL6974_DISCOVERY_NO_RUNTIME',
          'parent': ref(PARENT / 'freeze.json'), 'toolSource': sources, 'configurations': rows,
          'positiveCases': 108, 'negativeCases': 36, 'native62': freeze['native62'],
          'focusedFilter': 'FullyQualifiedName~Cadoryx.Tests.FiniteSurfaceExtrusionTests|FullyQualifiedName~Cadoryx.Tests.FiniteSurfaceNewBodyTests|FullyQualifiedName~Cadoryx.Tests.FiniteSurfaceAnalyticBranchTests',
          'baselineFilter': 'FullyQualifiedName~Cadoryx.Tests.FiniteSurfaceAnalyticBranchTests',
          'originalRawFar90FailuresChanged': False, 'originalStrictSTEP24Changed': False,
          'productionApplied': False, 'runtimeExecuted': False, 'AFComplete': False,
          'G': 'USER_DEFERRED', 'cleanup': False}
target = DRAFT / 'freeze.json'
assert not target.exists(), 'Preserve the original candidate freeze.'
target.write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'configurations': 2, 'actualDiscovered': 6974, 'retainedParent': 6830,
                  'addedPositive': 108, 'addedNegative': 36, 'runtimeExecuted': False}))
