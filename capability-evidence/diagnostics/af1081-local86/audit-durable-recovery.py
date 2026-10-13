from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import re

ROOT = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent
DRAFT = ROOT / 'artifacts/af1081-local86-1'
def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()
def ref(p): return {'path': p.relative_to(ROOT).as_posix(), 'sha256': sha(p)}
source = ROOT / 'tools/CadRecoveryWindowProbe'
for label, name, original, expected in [
    ('ViewportRecovery', 'Program.cs', 'recovery24', 'ADBA80081EF3CBE7324C9AF55FA7378363E18F931F1003BA37A2D5CE744B1B2C'),
    ('MainStatusRecovery', 'StartupHook.cs', 'main-status36', 'B7B5697C01F1074B531949E11E9C73ECBD5EA39E6630CB3FCBE3B5EB9B808AFB')]:
    assert sha(source / label / name) == sha(DRAFT / original / 'source' / name) == expected
freeze = read(DRAFT / 'freeze.json')
native = {r['name']: r['sha256'] for r in freeze['native62']}
sdk = {r['name']: r['sha256'] for r in freeze['managed13']}
stages = read(DRAFT / 'durable-recovery-build-exit.json')['stages']
assert len(stages) == 4 and all(r['exitCode'] == 0 for r in stages)
configs = []
for s in stages:
    app_config = next(c for c in freeze['configurations'] if c['configuration'] == s['configuration'])
    app = Path(app_config['candidate'])
    for r in app_config['appPayload']: assert sha(app / r['path']) == r['sha256']
    log = Path(s['log']).read_text(encoding='utf-8-sig')
    assert not re.search(r'\b(?:warning|error)\s+(?:[A-Z]+\d+|:)', log, re.IGNORECASE)
    output = Path(s['output'])
    if s['label'] == 'ViewportRecovery':
        assert len(list(app.glob('Cadoryx.*.dll'))) == 19
        for p in app.glob('Cadoryx.*.dll'): assert sha(output / p.name) == sha(p)
        assert {p.name: sha(p) for p in output.glob('OcctSharp*.dll')} == sdk
        assert {p.name: sha(p) for p in (output / 'occt').glob('*.dll')} == native
        for name in ['Cadoryx.wpf.exe', 'Cadoryx.wpf.deps.json', 'Cadoryx.wpf.runtimeconfig.json']:
            assert sha(output / name) == sha(app / name)
        for p in app.glob('*/Cadoryx.Lang.resources.dll'): assert sha(output / p.parent.name / p.name) == sha(p)
    configs.append(dict(s, payload=[{'path': p.relative_to(output).as_posix(), 'sha256': sha(p)}
                                    for p in sorted(output.rglob('*')) if p.is_file()]))
result = {'recordedAt': datetime.now(timezone.utc).isoformat(),
          'state': 'DURABLE_ORIGINAL_RECOVERY24_MAIN36_BOTH_PUBLISH0_CURRENT1081_LOCAL86_NOT_RUNTIME',
          'sources': [ref(p) for p in sorted(source.rglob('*')) if p.is_file() and p.suffix in ['.cs', '.csproj', '.props', '.targets', '.md'] and not {'bin', 'obj'} & set(p.parts)],
          'configurations': configs, 'originalCSharpByteIdentical': True,
          'application19WorkerLanguageSdk13Native62Matched': True,
          'runtimeBodiesAndOriginalConditionsRetained': True,
          'actualRuntime': False, 'actualDesktopInput': False, 'AFComplete': False,
          'productionApplied': False, 'publiclyPublished': False, 'G': 'USER_DEFERRED'}
target = OUT / 'durable-recovery-verification.json'
assert not target.exists()
target.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
print(json.dumps({'durableProjects': 2, 'publishStages0': 4, 'csharpByteIdentical': True, 'currentPayloadMatched': True, 'runtime': 'NOT RUN'}))
