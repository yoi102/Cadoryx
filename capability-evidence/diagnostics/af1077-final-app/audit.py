from pathlib import Path
from datetime import datetime, timezone
import hashlib, json, shutil

root = Path('C:/Users/yoiri/source/repos/Cadoryx')
draft = root / 'artifacts/af1077-drawing-action-1'
dest = root / 'capability-evidence/diagnostics/af1077-final-app'
assert not dest.exists()
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
prepared = read(draft / 'preparation.json')
parent = read(root / 'artifacts/af1076-root-full-1/freeze.json')
perf = read(draft / 'performance-freeze.json')
workspace = Path(prepared['workspace'])
original = {p['path']: p['sha256'] for p in parent['sourcePayload']}
changed = []
for p in prepared['payload']:
    assert sha(workspace / p['path']) == p['sha256']
    assert sha(root / p['path']) == original[p['path']]
    if p['sha256'] != original[p['path']]: changed.append(p['path'])
assert changed == ['Cadoryx.ViewModels/Toolboxes/CadAgentDrawingTools.cs']
for c in perf['configurations']:
    app = Path(c['candidate'])
    for p in c['payload']: assert sha(app / p['path']) == p['sha256']
runtime = read(draft / 'final-app-managed-result.json')
assert len(runtime['rows']) == 90 and all(r['outcome'] == 'Passed' for r in runtime['rows'])
for c in runtime['configurations']:
    assert len([r for r in runtime['rows'] if r['config'] == c['config']]) == 45 and c['nativeModules'] == []
    assert len(c['raw']) == len(c['wire']) == 1326 and prepared['after'] in c['wire']
    vm = next(a for a in c['assemblies'] if a['name'] == 'Cadoryx.ViewModels')
    app = next(x for x in perf['configurations'] if x['configuration'] == c['config'])
    assert Path(vm['location']) == Path(app['candidate']) / 'Cadoryx.ViewModels.dll'
    for a in c['assemblies']: assert sha(Path(a['location'])) == a['sha256']
    tests = next(a for a in c['assemblies'] if a['name'] == 'Cadoryx.Tests')
    oldConfig = next(x for x in parent['configurations'] if x['configuration'] == c['config'])
    assert tests['sha256'] == next(p['sha256'] for p in oldConfig['payload'] if p['path'] == 'Cadoryx.Tests.dll')
provider = draft / 'provider2'
assert all(s['exitCode'] == 0 for s in read(draft / 'final-provider-build-exit.json')['stages'])
app = Path(perf['configurations'][0]['candidate'])
shared = []
for p in provider.glob('Cadoryx.*.dll'):
    assert sha(p) == sha(app / p.name)
    shared.append({'name': p.name, 'sha256': sha(p)})
assert len(shared) == 16
metadata = read(draft / 'build-metadata-comparison.json')
assert metadata[0]['informationalVersion'] != metadata[1]['informationalVersion'] == metadata[2]['informationalVersion']
sources = []
for name in ('Program.cs', 'DrawingProviderProbe.cs', 'WorkspaceProviderProbe.cs'):
    p = workspace / 'tools/CadAgentWorkflowProbe' / name
    assert sha(p) == sha(root / 'tools/CadAgentWorkflowProbe' / name)
    sources.append({'name': name, 'sha256': sha(p)})
providerPayload = [{'path': p.relative_to(provider).as_posix(), 'sha256': sha(p)} for p in sorted(provider.rglob('*')) if p.is_file()]
sdk = {p['path']: p['sha256'] for p in perf['configurations'][0]['payload'] if p['path'].startswith('OcctSharp') and '/' not in p['path'] and p['path'].endswith('.dll')}
assert len(sdk) == 13
for name, expected in sdk.items(): assert sha(provider / name) == expected
for p in parent['native62']: assert sha(provider / 'occt' / p['name']) == p['sha256']
dest.mkdir(parents=True)
files = []
names = ('performance-freeze.json', 'wpf-build-exit.json', 'wpf-release.log', 'wpf-debug.log', 'final-app-managed-result.json',
         'final-provider-build-exit.json', 'final-provider-publish.log', 'build-metadata-comparison.json', 'provider-queue-2-stopped-before-native.json')
copies = [(draft / name, name) for name in names] + [(Path(__file__), 'audit.py'), (draft / 'probe-final-app/Program.cs', 'ManagedFinalAppProbe.cs')]
for p, name in copies:
    shutil.copyfile(p, dest / name)
    files.append({'path': name, 'sha256': sha(dest / name)})
proof = {'recordedAt': datetime.now(timezone.utc).isoformat(), 'state': 'AF1077_FINAL_WPF_BOTH0_ACTUAL_APP45_BOTH_PROVIDER16_BYTE_EQUAL_NOT_RUNTIME_ACCEPTANCE',
         'sourceInputs': 1189, 'changed': changed, 'original6652TestSourcesRetained': True,
         'actualApplicationOriginalAssertionsPerConfiguration': 45, 'fullXunitRun': False, 'nativeJobs': 0,
         'sharedApplication16': shared, 'provider': str(provider), 'providerPayload': providerPayload,
         'originalProviderSource3': sources, 'publicSDK13Native62DiskMatch': True,
         'performanceFreeze': {'path': str(draft / 'performance-freeze.json'), 'sha256': sha(draft / 'performance-freeze.json')},
         'originalSourceAndPerformanceConditionsUnchanged': True, 'modelWorkflow': 'NOT RUN',
         'actualPerformanceRun': False, 'productionApplied': False, 'originalProviderMetadataDifferenceObserved': True,
         'references': files, 'AFComplete': False, 'cleanup': False, 'G': 'USER_DEFERRED'}
(dest / 'verification.json').write_text(json.dumps(proof, indent=2), encoding='utf-8')
print(json.dumps({'WPF': 'both build0', 'actualAppCases': '45/45 both', 'provider16Equal': True, 'performance': 'NOT RUN'}))
