from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import re
import shutil
import sys

ROOT = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent
DRAFT = ROOT / 'artifacts/af1081-local86-1'
def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()
def ref(p): return {'path': p.relative_to(ROOT).as_posix(), 'sha256': sha(p)}
def write(p, value):
    assert not p.exists(), p
    p.write_text(json.dumps(value, indent=2) + '\n', encoding='utf-8')
freeze = read(DRAFT / 'freeze.json')
mode = sys.argv[1]
if mode == 'prepare':
    old24 = read(ROOT / 'artifacts/af1063r5-wpf-recovery24-draft-1/preparation-1.json')
    old36 = read(ROOT / 'artifacts/af1063r5-main-status-recovery-draft-1/build-2-verification.json')
    rows = []
    for label, source, expected in [
        ('recovery24', Path(old24['source']), {Path(r['current']['path']).name: r['current']['sha256'] for r in old24['sources']}),
        ('main-status36', Path(old36['sources'][0]['path']).parent, {Path(r['path']).name: r['sha256'] for r in old36['sources']})]:
        dest = DRAFT / label / 'source'
        dest.mkdir(parents=True, exist_ok=False)
        for name, expected_hash in expected.items():
            original = source / name
            assert sha(original) == expected_hash
            target = dest / name
            shutil.copyfile(original, target)
            if name.endswith('.csproj'):
                text = target.read_text(encoding='utf-8-sig')
                prefix = 'C:/Users/yoiri/source/repos/Cadoryx/artifacts/af-sdk30-history-identity-draft-5/workspace-1/Cadoryx.wpf/bin/Release/net10.0-windows/win-x64'
                assert prefix in text
                text = text.replace(prefix, '$(CandidatePayload)')
                if label == 'recovery24':
                    # Current application kernels may start the frozen worker.
                    text = text.replace('  </ItemGroup>', '''    <Content Include="$(CandidatePayload)/Cadoryx.wpf.exe;$(CandidatePayload)/Cadoryx.wpf.deps.json;$(CandidatePayload)/Cadoryx.wpf.runtimeconfig.json"><Link>%(Filename)%(Extension)</Link><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></Content>
    <Content Include="$(CandidatePayload)/**/*.resources.dll"><Link>%(RecursiveDir)%(Filename)%(Extension)</Link><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></Content>
  </ItemGroup>''')
                target.write_text(text, encoding='utf-8')
            rows.append({'label': label, 'source': ref(original), 'candidate': ref(target),
                         'csharpByteIdentical': sha(original) == sha(target) if name.endswith('.cs') else None})
    assert all(r['csharpByteIdentical'] for r in rows if r['csharpByteIdentical'] is not None)
    write(DRAFT / 'recovery-preparation.json', {
        'recordedAt': datetime.now(timezone.utc).isoformat(), 'sources': rows,
        'original24ProgramAnd36HookByteIdentical': True, 'original36Matrix': old36['matrix'],
        'original24': 24, 'readinessSecondsEach': 30, 'observationDeadlineSeconds': 900,
        'original24Saves': 48, 'original24WholeImages': 52, 'original36WholeImages': 144,
        'nativeRuntimeStarted': False, 'desktopInputStarted': False, 'productionApplied': False})
    print(json.dumps({'recoveryCases': 60, 'originalCSharpRetained': True, 'runtime': 'NOT RUN'}))

elif mode == 'audit-build':
    preparation = read(DRAFT / 'recovery-preparation.json')
    for r in preparation['sources']:
        for key in ['source', 'candidate']: assert sha(ROOT / r[key]['path']) == r[key]['sha256']
    stages = read(DRAFT / 'recovery-build-exit.json')['stages']
    assert len(stages) == 4 and all(r['exitCode'] == 0 for r in stages)
    native = {r['name']: r['sha256'] for r in freeze['native62']}
    sdk = {r['name']: r['sha256'] for r in freeze['managed13']}
    configs = []
    for stage in stages:
        text = Path(stage['log']).read_text(encoding='utf-8-sig')
        # dotnet publish omits the build summary; retain its complete output
        # and verify emitted diagnostics, without inventing summary counts.
        assert not re.search(r'\b(?:warning|error)\s+(?:[A-Z]+\d+|:)', text, re.IGNORECASE), stage['log']
        app_config = next(c for c in freeze['configurations'] if c['configuration'] == stage['configuration'])
        app = Path(app_config['candidate'])
        for r in app_config['appPayload']: assert sha(app / r['path']) == r['sha256']
        output = Path(stage['output'])
        if stage['label'] == 'recovery24':
            production = sorted(app.glob('Cadoryx.*.dll'))
            assert len(production) == 19
            for p in production: assert sha(output / p.name) == sha(p)
            assert {p.name: sha(p) for p in output.glob('OcctSharp*.dll')} == sdk
            assert {p.name: sha(p) for p in (output / 'occt').glob('*.dll')} == native
            for name in ['Cadoryx.wpf.exe', 'Cadoryx.wpf.deps.json', 'Cadoryx.wpf.runtimeconfig.json']:
                assert sha(output / name) == sha(app / name)
            for p in app.glob('*/Cadoryx.Lang.resources.dll'): assert sha(output / p.parent.name / p.name) == sha(p)
        configs.append(dict(stage, payload=[{'path': p.relative_to(output).as_posix(), 'sha256': sha(p)}
                                           for p in sorted(output.rglob('*')) if p.is_file()]))
    write(OUT / 'recovery-build-verification.json', {
        'recordedAt': datetime.now(timezone.utc).isoformat(),
        'state': 'ORIGINAL_RECOVERY24_MAIN_STATUS36_BOTH_BUILD0_CURRENT1081_LOCAL86_NOT_RUNTIME',
        'preparation': ref(DRAFT / 'recovery-preparation.json'), 'freeze': ref(DRAFT / 'freeze.json'),
        'configurations': configs, 'originalCSharpAndAllConditionsRetained': True,
        'actualNativeRuntime': False, 'actualDesktopRun': False, 'AFComplete': False,
        'productionApplied': False, 'publiclyPublished': False, 'G': 'USER_DEFERRED'})
    print(json.dumps({'bothRecoveryHostsBothBuild0': True, 'currentApplication19AndSdk13Native62': True,
                      'original24And36': True, 'runtime': 'NOT RUN'}))
