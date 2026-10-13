from pathlib import Path
from datetime import datetime, timezone
import hashlib, json, shutil

root = Path('C:/Users/yoiri/source/repos/Cadoryx')
draft = root / 'artifacts/af1077-drawing-action-1'
dest = root / 'capability-evidence/diagnostics/af1077-drawing-action'
assert not dest.exists(), 'Immutable evidence exists'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
freeze = read(root / 'artifacts/af1076-root-full-1/freeze.json')
prepared = read(draft / 'preparation.json')
workspace = Path(prepared['workspace'])
assert len(prepared['payload']) == len(freeze['sourcePayload']) == 1189
original = {r['path']: r['sha256'] for r in freeze['sourcePayload']}
changed = []
for row in prepared['payload']:
    assert sha(workspace / row['path']) == row['sha256']
    assert sha(root / row['path']) == original[row['path']]
    if row['sha256'] != original[row['path']]: changed.append(row['path'])
assert changed == ['Cadoryx.ViewModels/Toolboxes/CadAgentDrawingTools.cs']
old = (root / changed[0]).read_bytes()
new = (workspace / changed[0]).read_bytes()
assert len(prepared['before']) == len(prepared['after']) == 67
assert new == old.replace(prepared['before'].encode(), prepared['after'].encode())
build = read(draft / 'build-exit.json')
assert len(build['stages']) == 3 and all(s['exitCode'] == 0 for s in build['stages'])
for stage in build['stages'][1:]:
    text = Path(stage['log']).read_text(encoding='utf-8-sig')
    assert '0 Warning(s)' in text and '0 Error(s)' in text
runtime = read(draft / 'managed-result.json')
assert len(runtime['rows']) == 90 and all(r['outcome'] == 'Passed' for r in runtime['rows'])
assert runtime['nativeJobs'] == 0 and not runtime['productionApplied']
for config in runtime['configurations']:
    assert len([r for r in runtime['rows'] if r['config'] == config['config']]) == 45
    assert config['nativeModules'] == []
    assert len(config['raw']) == len(config['wire']) == 1326
    assert read(Path(stage['log']).parent / 'preparation.json')['after'] in config['wire']
    for assembly in config['assemblies']:
        assert sha(Path(assembly['location'])) == assembly['sha256']
    vm = next(a for a in config['assemblies'] if a['name'] == 'Cadoryx.ViewModels')
    assert Path(vm['location']) == workspace / f'Cadoryx.ViewModels/bin/{config["config"]}/net10.0/Cadoryx.ViewModels.dll'
    tests = next(a for a in config['assemblies'] if a['name'] == 'Cadoryx.Tests')
    frozenConfig = next(c for c in freeze['configurations'] if c['configuration'] == config['config'])
    assert tests['sha256'] == next(p['sha256'] for p in frozenConfig['payload'] if p['path'] == 'Cadoryx.Tests.dll')
provider = draft / 'provider'
assert read(draft / 'provider-publish-exit.json')['exitCode'] == 0
release = next(c for c in freeze['configurations'] if c['configuration'] == 'Release')
sdk = {p['path']: p['sha256'] for p in release['payload'] if p['path'].startswith('OcctSharp') and '/' not in p['path'] and p['path'].endswith('.dll')}
assert len(sdk) == 13 and {p.name for p in provider.glob('OcctSharp*.dll')} == sdk.keys()
for name, expected in sdk.items(): assert sha(provider / name) == expected
assert {p.name for p in (provider / 'occt').glob('*.dll')} == {p['name'] for p in freeze['native62']}
for row in freeze['native62']: assert sha(provider / 'occt' / row['name']) == row['sha256']
assert sha(provider / 'Cadoryx.ViewModels.dll') == sha(workspace / 'Cadoryx.ViewModels/bin/Release/net10.0/Cadoryx.ViewModels.dll')
sources = []
for name in ('Program.cs', 'DrawingProviderProbe.cs', 'WorkspaceProviderProbe.cs'):
    p = workspace / 'tools/CadAgentWorkflowProbe' / name
    assert sha(p) == sha(root / 'tools/CadAgentWorkflowProbe' / name)
    sources.append({'name': name, 'sha256': sha(p)})
dest.mkdir(parents=True)
references = []
copies = [(draft / name, name) for name in ('managed-result.json', 'build-exit.json', 'build-release.log', 'build-debug.log', 'provider-publish-exit.json', 'provider-publish.log')]
copies += [(workspace / changed[0], 'CadAgentDrawingTools.cs'), (Path(__file__), 'audit.py'), (draft / 'probe/Program.cs', 'ManagedProbe.cs')]
for path, name in copies:
    shutil.copyfile(path, dest / name)
    references.append({'path': name, 'sha256': sha(dest / name)})
providerPayload = [{'path': p.relative_to(provider).as_posix(), 'sha256': sha(p)} for p in sorted(provider.rglob('*')) if p.is_file()]
proof = {'recordedAt': datetime.now(timezone.utc).isoformat(), 'state': 'AF1077_ACTION_HINT_BUILD0_BOTH_ORIGINAL45_ASSERTIONS_BOTH_PASS_NOT_MODEL_ACCEPTANCE',
         'candidate': str(workspace), 'source1076FilesVerified': 1189, 'changed': changed,
         'before': prepared['before'], 'after': prepared['after'], 'rawAndWireCharacters': 1326,
         'original6652TestSourcesRetained': True, 'originalCompiledAssertionCasesPerConfiguration': 45,
         'fullXunitRun': False, 'productionApplied': False, 'nativeJobs': 0,
         'provider': str(provider), 'providerPayload': providerPayload, 'originalProviderSource3': sources,
         'publicSDK13AndNative62DiskMatch': True, 'modelWorkflow': 'NOT RUN', 'AFComplete': False,
         'references': references, 'cleanup': False, 'G': 'USER_DEFERRED'}
(dest / 'verification.json').write_text(json.dumps(proof, indent=2), encoding='utf-8')
print(json.dumps({'changedFiles': len(changed), 'bothOriginalManagedAssertions': '45/45', 'providerSDK13Native62': True, 'model': 'NOT RUN'}))
