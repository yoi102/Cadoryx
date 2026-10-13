from pathlib import Path
from datetime import datetime, timezone
import hashlib, json, shutil

root = Path('C:/Users/yoiri/source/repos/Cadoryx')
draft = root / 'artifacts/af1077-drawing-action-1'
dest = root / 'capability-evidence/diagnostics/af1077-window-observer'
assert not dest.exists()
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
oldProof = read(root / 'capability-evidence/diagnostics/af1074-public85/window-startup-and-trace-verification.json')
oldSource = root / 'artifacts/af1074-public85-1/window-observer-revision3/WindowHost.cs'
assert sha(oldSource) == oldProof['durableHostSource']['sha256']
newSource = root / 'tools/CadFiniteSurfaceWindowProbe/WindowHost.cs'
old = oldSource.read_text(encoding='utf-8-sig')
new = newSource.read_text(encoding='utf-8-sig')

def calls(text, name):
    results, at = [], 0
    while True:
        start = text.find(name + '(', at)
        if start < 0: return results
        pos, depth, quote = start + len(name), 0, None
        while pos < len(text):
            c = text[pos]
            if quote:
                if c == '\\': pos += 2; continue
                if c == quote: quote = None
            elif c in ('"', "'"): quote = c
            elif c == '(': depth += 1
            elif c == ')':
                depth -= 1
                if depth == 0:
                    results.append(text[start:pos+1]); at = pos+1; break
            pos += 1
        else: raise AssertionError('Unbalanced original assertion call')

assert calls(old, 'Require') == calls(new, 'Require')
assert calls(old, 'evidence.Add') == calls(new, 'evidence.Add')
assert '<30' in new and '<300' in new and 'SetEquals(new[]{"INVENTORY_CLEARED_EXPLICIT_SELECTION"' in new
freeze = read(root / 'artifacts/af1076-root-full-1/freeze.json')
for p in freeze['sourcePayload']: assert sha(root / p['path']) == p['sha256']
assert not any(p['path'].startswith('tools/CadFiniteSurfaceWindowProbe/') for p in freeze['sourcePayload'])
build = read(draft / 'window-observer-4-build-exit.json')
assert len(build['stages']) == 2 and all(s['exitCode'] == 0 for s in build['stages'])
perf = read(draft / 'performance-freeze.json')
configs = []
for s in build['stages']:
    text = Path(s['log']).read_text(encoding='utf-8-sig')
    assert '0 Warning(s)' in text and '0 Error(s)' in text
    app = Path(s['candidate'])
    host = draft / f'window-observer-4/bin/{s["configuration"]}/net10.0-windows/win-x64'
    cfg = next(c for c in perf['configurations'] if c['configuration'] == s['configuration'])
    expected = {p['path']: p['sha256'] for p in cfg['payload']}
    refs = []
    for p in app.glob('Cadoryx.*.dll'):
        assert sha(host / p.name) == expected[p.name] == sha(p)
        refs.append(p.name)
    expectedApp = {p for p in expected if p.startswith('Cadoryx.') and '/' not in p and p.endswith('.dll')}
    assert len(refs) == len(expectedApp) == 19 and set(refs) == expectedApp
    for name in ('Cadoryx.wpf.exe', 'Cadoryx.wpf.deps.json', 'Cadoryx.wpf.runtimeconfig.json'):
        assert sha(host / name) == expected[name]
    sdk = {p.name for p in app.glob('OcctSharp*.dll')}
    assert len(sdk) == 13 and {p.name for p in host.glob('OcctSharp*.dll')} == sdk
    for name in sdk: assert sha(host / name) == expected[name]
    for p in cfg['native']: assert sha(host / 'occt' / p['name']) == p['sha256']
    for lang in ('en-US', 'zh-CN', 'ja-JP'):
        resource = app / lang / 'Cadoryx.Lang.resources.dll'
        if resource.exists(): assert sha(host / lang / resource.name) == sha(resource)
    configs.append({'configuration': s['configuration'], 'productionAssembliesMatched': len(refs),
                   'workerExeAndConfigurationMatched': True, 'sdk13Native62DiskMatched': True,
                   'host': str(host), 'binarySha256': sha(host / 'WindowHost.dll')})
matrix = read(root / 'capability-evidence/diagnostics/af1074-public85/window-matrix-preparation.json')['matrix']
assert len(matrix) == 36 and len({(m['phase'], m['language'], m['label'], m['route']) for m in matrix}) == 36
entries = []
for m in matrix:
    assert sha(Path(m['fixture']['path'])) == m['fixture']['sha256']
    entry = {k: m[k] for k in ('phase', 'language', 'label', 'route', 'fixture')}
    entry['output'] = str(draft / f'window-{m["phase"]}-{m["language"]}-{m["label"]}-4')
    assert not Path(entry['output']).exists()
    entry['state'] = 'NOT RUN'
    entries.append(entry)
dest.mkdir(parents=True)
references = []
copies = [(newSource, 'WindowHost.cs'), (root / 'tools/CadFiniteSurfaceWindowProbe/README.md', 'README.md'),
          (draft / 'window-observer-4-build-exit.json', 'build-exit.json'), (Path(__file__), 'audit.py')]
copies += [(Path(s['log']), s['configuration'].lower() + '-build.log') for s in build['stages']]
for p, name in copies:
    shutil.copyfile(p, dest / name)
    references.append({'path': name, 'sha256': sha(dest / name)})
proof = {'recordedAt': datetime.now(timezone.utc).isoformat(), 'state': 'D6_OBSERVER4_BOTH_BUILD0_READ_ONLY_FOCUS_DIAGNOSTICS_NOT_WINDOW_ACCEPTANCE',
         'originalRequireCallsRetained': len(calls(old, 'Require')), 'originalEvidenceAddCallsRetained': len(calls(old, 'evidence.Add')),
         'originalSixStatesAnd30And300SecondsRetained': True, 'configurations': configs,
         'original36MatrixAndExactFixturesRetained': True, 'matrix': entries, 'productionSource1189Unchanged': True,
         'productionDialogChanged': False, 'hostInjectsInput': False, 'actualDesktopRun': False,
         'inputTargetMustComeFromReturnedComputerUseWindow': True, 'nativeHandlesDiagnosticsOnly': True,
         'nativeLane': 'After original Debug and model scenarios, hand back to SDK; observer not queued for native execution',
         'references': references, 'AFComplete': False, 'cleanup': False, 'G': 'USER_DEFERRED'}
(dest / 'verification.json').write_text(json.dumps(proof, indent=2), encoding='utf-8')
print(json.dumps({'requireCalls': proof['originalRequireCallsRetained'], 'cases': 36, 'bothBuild0': True, 'desktop': 'NOT RUN'}))
