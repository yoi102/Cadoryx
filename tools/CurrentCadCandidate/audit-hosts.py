from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import re

ROOT = Path(__file__).resolve().parents[2]
DRAFT = ROOT / 'artifacts/af1082-f5-d7-local87cu1-1'
OUT = ROOT / 'capability-evidence/diagnostics/af1082-f5-d7-local87cu1'
def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()
def ref(p): return {'path': p.relative_to(ROOT).as_posix(), 'sha256': sha(p)}
def payload(folder): return [{'path': p.relative_to(folder).as_posix(), 'sha256': sha(p)}
                            for p in sorted(folder.rglob('*')) if p.is_file()]

freeze = read(DRAFT / 'freeze.json')
old_path = ROOT / 'capability-evidence/diagnostics/af1081-local86/durable-recovery-verification.json'
old = read(old_path)
for row in old['sources']:
    assert sha(ROOT / row['path']) == row['sha256']
native = {r['name']: r['sha256'] for r in freeze['native62']}
sdk = {r['name']: r['sha256'] for r in freeze['managed13']}
stages = read(DRAFT / 'hosts-build-exit.json')['stages']
assert len(stages) == 6 and all(r['exitCode'] == 0 for r in stages)
configs = []
for stage in stages:
    app = Path(stage['candidate']); folder = Path(stage['output'])
    config = next(c for c in freeze['configurations'] if c['configuration'] == stage['configuration'])
    assert app == Path(config['candidate'])
    for row in config['appPayload']: assert sha(app / row['path']) == row['sha256']
    log = Path(stage['log']).read_text(encoding='utf-8-sig')
    assert not re.search(r'\b(?:warning|error)\s+(?:[A-Z]+\d+|:)', log, re.I)
    if stage['label'] != 'main-status36':
        production = sorted(app.glob('Cadoryx.*.dll')); assert len(production) == 19
        for p in production: assert sha(folder / p.name) == sha(p)
        assert {p.name: sha(p) for p in folder.glob('OcctSharp*.dll')} == sdk
        assert {p.name: sha(p) for p in (folder / 'occt').glob('*.dll')} == native
        for name in ['Cadoryx.wpf.exe', 'Cadoryx.wpf.deps.json', 'Cadoryx.wpf.runtimeconfig.json']:
            assert sha(folder / name) == sha(app / name)
        for p in app.glob('*/Cadoryx.Lang.resources.dll'):
            assert sha(folder / p.parent.name / p.name) == sha(p)
    configs.append(dict(stage, payload=payload(folder), compiledAgainst=ref(DRAFT / 'freeze.json')))
record = {'recordedAt': datetime.now(timezone.utc).isoformat(),
          'state': 'CURRENT_F5_D7_BOTH_RECOVERY24_MAIN36_D6WINDOW36_HOSTS_PUBLISH0_NOT_RUNTIME',
          'freeze': ref(DRAFT / 'freeze.json'), 'originalRecoverySources': ref(old_path),
          'configurations': configs, 'originalRecoveryCSharpByteIdentical': True,
          'windowObserverSources': [ref(ROOT / 'tools/CadFiniteSurfaceWindowProbe/WindowHost.cs'),
                                    ref(ROOT / 'tools/CadFiniteSurfaceWindowProbe/WindowHost.csproj')],
          'originalRecovery24AndMain36BudgetsAndAssertionsChanged': False,
          'originalD6Window36MatrixAndBudgetsChanged': False,
          'recovery24ReadinessSeconds': 30, 'recoveryObservationSeconds': 900,
          'recovery24Saves': 48, 'recovery24Images': 52, 'main36Images': 144,
          'D6ReadinessSeconds': 30, 'D6ObservationSeconds': 300,
          'actualNativeRuntimeStarted': False, 'desktopInputStarted': False, 'AFComplete': False}
OUT.mkdir(parents=True, exist_ok=True)
target = OUT / 'hosts-verification.json'; assert not target.exists()
target.write_bytes((json.dumps(record, indent=2) + '\n').encode('utf-8'))
print(json.dumps({'hostConfigurations': 6, 'exitCodes0': True, 'originalRecoveryCSharp': True,
                  'application19Sdk13Native62AndWorkerMatched': True, 'runtime': 'NOT RUN'}))
