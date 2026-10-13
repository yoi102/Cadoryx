from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[3]
DRAFT = ROOT / 'artifacts/af1081-local86-1'
WORK = DRAFT / 'workspace'
VERSION = '8.0.1-preview.86'
PROOF = Path(__file__).resolve().parent
def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()
def write(p, value):
    assert not p.exists(), p
    p.write_text(json.dumps(value, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
def files(folder):
    return [{'path': p.relative_to(folder).as_posix(), 'sha256': sha(p)}
            for p in sorted(folder.rglob('*')) if p.is_file()]

mode = sys.argv[1]
sdk = read(ROOT / 'capability-evidence/diagnostics/local-sdk86-received/verification.json')
feed = Path(sdk['immutableReceivedFeed'])
native = {Path(r['entry']).name: r['sha256'] for r in sdk['native62']}
managed = {Path(r['entry']).name: r['sha256'] for r in sdk['managed13']}
assert len(native) == 62 and len(managed) == 13
for row in sdk['packages']:
    assert sha(feed / Path(row['path']).name) == row['sha256']

if mode == 'prepare':
    old = read(ROOT / 'artifacts/af1080-final-1/preparation.json')
    paths = [r['path'] for r in old['payload']] + ['Cadoryx.Tests/MainWindowAttachmentFailureTests.cs']
    assert len(paths) == len(set(paths)) == 1193
    changes = [r['path'] for r in old['payload'] if sha(ROOT / r['path']) != r['sha256']]
    assert changes == ['Cadoryx.ViewModels/MainWindowViewModel.cs'], changes
    WORK.mkdir(parents=True, exist_ok=False)
    original = []
    for path in paths:
        source = ROOT / path
        dest = WORK / path
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, dest)
        original.append({'path': path, 'sha256': sha(source)})
    props = WORK / 'Directory.Build.props'
    props.write_text(props.read_text(encoding='utf-8-sig').replace('8.0.1-preview.85', VERSION), encoding='utf-8')
    (WORK / 'NuGet.Config').write_text('''<?xml version="1.0" encoding="utf-8"?>
<configuration>
 <packageSources><clear/><add key="local86" value="''' + str(feed) + '''"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
 <packageSourceMapping><packageSource key="local86"><package pattern="OcctSharp*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>
''', encoding='utf-8')
    baseline = read(WORK / 'Cadoryx.wpf/runtime-baseline.json')
    baseline['occtSharpPackage'] = VERSION
    baseline['nativeSha256'] = native['OcctSharp.Native.dll'].lower()
    (WORK / 'Cadoryx.wpf/runtime-baseline.json').write_text(json.dumps(baseline, indent=2) + '\n', encoding='utf-8')
    projects = [p for p in paths if p.endswith('.csproj')]
    assert len(projects) == 50
    (WORK / 'CandidateAllProjects.slnx').write_text('<Solution>\n' + ''.join(f' <Project Path="{p}"/>\n' for p in projects) + '</Solution>\n', encoding='utf-8')
    old_freeze = read(ROOT / 'artifacts/af1080-final-1/freeze.json')
    for row in old_freeze['originalModelPromptAndAssertionSources']:
        assert sha(WORK / 'tools/CadAgentWorkflowProbe' / Path(row['path']).name) == row['sha256']
    head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
    write(DRAFT / 'preparation.json', {
        'recordedAt': datetime.now(timezone.utc).isoformat(), 'baseHead': head,
        'workspace': str(WORK), 'version': VERSION, 'originalSource': original,
        'candidateBeforeRestore': [{'path': p, 'sha256': sha(WORK / p)} for p in paths],
        'projects': projects, 'expectedTestCount': 6830, 'fullTestFilter': old_freeze['fullTestFilter'],
        'native62': [{'name': n, 'sha256': h} for n, h in native.items()],
        'managed13': [{'name': n, 'sha256': h} for n, h in managed.items()],
        'productionApplied': False, 'publiclyPublished': False, 'AFComplete': False})
    print(json.dumps({'inputs': len(paths), 'projects': len(projects), 'workspace': str(WORK)}))

elif mode == 'locks':
    prep = read(DRAFT / 'preparation.json')
    locks = []
    for row in prep['originalSource']:
        path = row['path']
        if not path.endswith('packages.lock.json'): continue
        before, after = read(ROOT / path), read(WORK / path)
        assert sha(ROOT / path) == row['sha256']
        assert before['dependencies'].keys() == after['dependencies'].keys()
        sdk_count = 0
        for framework, dependencies in before['dependencies'].items():
            updated = after['dependencies'][framework]
            assert dependencies.keys() == updated.keys(), path
            for name, entry in dependencies.items():
                actual = updated[name]
                if name.startswith('OcctSharp'):
                    assert actual['resolved'] == VERSION, (path, name)
                    if 'requested' in actual: assert actual['requested'] == f'[{VERSION}, {VERSION}]'
                    for dep, constraint in actual.get('dependencies', {}).items():
                        if dep.startswith('OcctSharp'): assert constraint == f'[{VERSION}]', (path, name, dep, constraint)
                    sdk_count += 1
                elif entry.get('type') == 'Project':
                    b, a = dict(entry), dict(actual)
                    b['dependencies'] = {n: v for n, v in b.get('dependencies', {}).items() if not n.lower().startswith('occtsharp')}
                    a['dependencies'] = {n: v for n, v in a.get('dependencies', {}).items() if not n.lower().startswith('occtsharp')}
                    assert a == b, (path, name)
                else: assert actual == entry, (path, name)
        locks.append({'path': path, 'sha256': sha(WORK / path), 'sdkReferences': sdk_count})
    write(DRAFT / 'locks.json', {'locks': locks, 'nonOcctDependenciesUnchanged': True})
    print(json.dumps({'locks': len(locks), 'sdkReferences': sum(r['sdkReferences'] for r in locks)}))

elif mode == 'freeze':
    prep = read(DRAFT / 'preparation.json')
    locks = read(DRAFT / 'locks.json')
    stages = read(DRAFT / 'build-exit.json')['stages']
    assert len(stages) == 7 and all(r['exitCode'] == 0 for r in stages)
    allowed = {'Directory.Build.props', 'NuGet.Config', 'Cadoryx.wpf/runtime-baseline.json'} | {r['path'] for r in locks['locks']}
    for row in prep['originalSource']:
        assert sha(ROOT / row['path']) == row['sha256']
        if row['path'] not in allowed: assert sha(WORK / row['path']) == row['sha256']
    configs = []
    for config in ['Release', 'Debug']:
        app = WORK / f'Cadoryx.wpf/bin/{config}/net10.0-windows/win-x64'
        test = WORK / f'Cadoryx.Tests/bin/{config}/net10.0'
        for folder in [app, test]:
            assert {p.name: sha(p) for p in folder.glob('OcctSharp*.dll')} == managed
            assert {p.name: sha(p) for p in (folder / 'occt').glob('*.dll')} == native
        discovery = (DRAFT / f'discovery-{config.lower()}.log').read_text(encoding='utf-8-sig')
        discovered = [s.strip() for s in discovery.splitlines() if s.strip().startswith('Cadoryx.Tests.')]
        assert len(discovered) == len(set(discovered)) == 6830, (config, len(discovered))
        configs.append({'configuration': config, 'candidate': str(app), 'appPayload': files(app),
                        'testDirectory': str(test), 'payload': files(test), 'discoveredNames': discovered})
    assert configs[0]['discoveredNames'] == configs[1]['discoveredNames']
    provider = DRAFT / 'provider'
    shared = [{'name': p.name, 'sha256': sha(p)} for p in sorted(provider.glob('Cadoryx.*.dll'))]
    assert len(shared) == 16
    for row in shared: assert sha(Path(configs[0]['candidate']) / row['name']) == row['sha256']
    assert {p.name: sha(p) for p in provider.glob('OcctSharp*.dll')} == managed
    assert {p.name: sha(p) for p in (provider / 'occt').glob('*.dll')} == native
    write(DRAFT / 'freeze.json', dict(prep, sourcePayload=[{'path': r['path'], 'sha256': sha(WORK / r['path'])} for r in prep['originalSource']],
                                    configurations=configs, provider=str(provider), providerPayload=files(provider), sharedProvider16=shared))
    print(json.dumps({'bothBuild0': True, 'bothDiscovered': 6830, 'sdk13native62Matched': True, 'providerShared16': True}))
