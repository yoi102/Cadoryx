"""Freeze the candidate after exact NuGet, companion and test-name checks."""
from pathlib import Path
from datetime import datetime, timezone
import base64
import hashlib
import json
import subprocess

ROOT = Path(__file__).resolve().parents[3]
TOOL = Path(__file__).resolve().parent
DRAFT = ROOT / 'artifacts/d7-analytic-branch-draft-4'
VERSION = '8.0.1-preview.87.cu1'
def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()
def ref(p): return {'path': p.relative_to(ROOT).as_posix(), 'sha256': sha(p)}
def payload(p): return [{'path': f.relative_to(p).as_posix(), 'sha256': sha(f)}
                        for f in sorted(p.rglob('*')) if f.is_file()]

assert not (DRAFT / 'freeze.json').exists()
parent_path = ROOT / 'artifacts/d7-analytic-branch-draft-1/freeze.json'
parent = read(parent_path)
original_path = ROOT / parent['parent']['path']
assert sha(original_path) == parent['parent']['sha256']
original = read(original_path)
for row in original['sourcePayload']:
    assert sha(Path(original['workspace']) / row['path']) == row['sha256']
receipt_path = ROOT / 'artifacts/local-sdk87cu1-received-1/receipt.json'
receipt = read(receipt_path)
gates_path = ROOT / 'artifacts/local-sdk87cu1-gates-received-1/receipt.json'
gates = read(gates_path)
assert gates['explicitNativeHandoffReceived'] and gates['version'] == VERSION
for row in gates['evidence']: assert sha(ROOT / row['path']) == row['sha256']
cache = DRAFT / 'nuget-cache'
for row in receipt['packages']:
    assert sha(Path(receipt['immutableReceivedFeed']) / row['name']) == row['sha256']
    package = cache / row['id'].lower() / VERSION / row['name'].lower()
    assert sha(package) == row['sha256'], str(package)

for name in ['BranchTests', 'KernelBranchCandidate']:
    assets = read(TOOL / 'obj' / name / 'project.assets.json')
    assert Path(assets['project']['restore']['packagesPath']) == cache
    sdk = {k.split('/')[0] for k, v in assets['libraries'].items()
           if k.startswith('OcctSharp') and v['type'] == 'package'}
    assert sdk == {r['id'] for r in receipt['packages']}
    for key, library in assets['libraries'].items():
        if key.startswith('OcctSharp'): assert key.endswith('/' + VERSION)
        if library['type'] == 'project':
            assert (TOOL / library['msbuildProject']).resolve() == TOOL / 'KernelBranchCandidate.csproj'
lock_path = TOOL / 'packages.lock.json'
for framework in read(lock_path)['dependencies'].values():
    for name, row in framework.items():
        if not name.startswith('OcctSharp'): continue
        assert row['resolved'] == VERSION
        package = next(r for r in receipt['packages'] if r['id'] == name)
        raw = (cache / name.lower() / VERSION / package['name'].lower()).read_bytes()
        assert base64.b64encode(hashlib.sha512(raw).digest()).decode() == row['contentHash']
        for dep, version in row.get('dependencies', {}).items():
            if dep.startswith('OcctSharp'): assert version == '[' + VERSION + ']'

rows = []
for config in ['Release', 'Debug']:
    build_path = DRAFT / ('build-' + config.lower() + '-2-exit.json')
    build = read(build_path)
    assert build['exitCode'] == 0 and build['version'] == VERSION
    log = (ROOT / build['log']).read_text(encoding='utf-8-sig')
    assert '0 Warning(s)' in log and '0 Error(s)' in log
    folder = Path(build['output'])
    companion = Path(original['configurations'][0]['testDirectory'])
    for row in receipt['managed13']: assert sha(folder / row['name']) == row['sha256']
    assert {p.name for p in folder.glob('OcctSharp*.dll')} == {r['name'] for r in receipt['managed13']}
    assert {p.name for p in (folder / 'occt').glob('*.dll')} == {r['name'] for r in receipt['native62']}
    for row in receipt['native62']: assert sha(folder / 'occt' / row['name']) == row['sha256']
    for p in folder.glob('*.dll'):
        if p.name.startswith('OcctSharp') or p.name in ['Cadoryx.Tests.dll', 'Cadoryx.Kernel.Occt.dll']: continue
        assert sha(p) == sha(companion / p.name), p.name
    runtime = read(folder / 'Cadoryx.Tests.runtimeconfig.json')['runtimeOptions']
    assert runtime['tfm'] == 'net10.0' and runtime['framework']['name'] == 'Microsoft.NETCore.App'
    discovery = DRAFT / ('discovery-' + config.lower() + '-1.log')
    assert not discovery.exists()
    result = subprocess.run(['dotnet', 'vstest', str(folder / 'Cadoryx.Tests.dll'), '/ListTests'],
                            cwd=ROOT, capture_output=True, encoding='utf-8', errors='replace')
    discovery.write_text(result.stdout + result.stderr, encoding='utf-8')
    assert result.returncode == 0, result.stdout + result.stderr
    names = {s.strip() for s in result.stdout.splitlines() if s.startswith('    Cadoryx.Tests.')}
    prior = next(c for c in parent['configurations'] if c['configuration'] == config)
    assert names == set(prior['discovered6974']) and len(names) == 6974
    rows.append({'configuration': config, 'build': ref(build_path), 'discovery': ref(discovery),
                 'testDirectory': str(folder), 'payload': payload(folder),
                 'discovered6974': sorted(names), 'added144': prior['added144'],
                 'companions': 'Frozen original Release companions in both configurations; not full application Debug qualification'})

record = {'recordedAt': datetime.now(timezone.utc).isoformat(),
          'state': 'D7_REVISION4_EXACT_NUGET_BOTH_BUILT_DISCOVERED_ORIGINAL6974_NOT_RUNTIME',
          'parent': ref(parent_path), 'originalSource': ref(original_path),
          'sdkReceivedBytes': ref(receipt_path), 'sdkReceivedGates': ref(gates_path),
          'version': VERSION, 'configurations': rows, 'native62': receipt['native62'],
          'focusedFilter': parent['focusedFilter'], 'lock': ref(lock_path),
          'toolSource': [ref(p) for p in sorted(TOOL.iterdir()) if p.is_file()],
          'sharedTests': ref(TOOL.parent / 'FiniteSurfaceAnalyticBranchTests.cs'),
          'baselineEvidence': ref(ROOT / 'artifacts/d7-analytic-branch-draft-1/focused-verification.json'),
          'completeStartHelperWired': True, 'newPlanarWireFaceAPIReceived': True,
          'geometryBudgetsChanged': False, 'runtimeExecuted': False,
          'productionApplied': False, 'AFComplete': False}
(DRAFT / 'freeze.json').write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'configurations': 2, 'actualDiscovered': 6974, 'focused': 243,
                  'exactSDK14Managed13Native62': True, 'runtimeExecuted': False}))
