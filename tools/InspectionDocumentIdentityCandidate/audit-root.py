from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json

ROOT = Path(__file__).resolve().parents[2]
TOOL = Path(__file__).resolve().parent
DRAFT = ROOT / 'artifacts/inspection-document-id-root-1'
OUT = ROOT / 'capability-evidence/diagnostics/inspection-document-id'
def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()
def ref(p): return {'path': p.relative_to(ROOT).as_posix(), 'sha256': sha(p)}

applied = read(DRAFT / 'applied.json')
for row in applied['files']: assert sha(ROOT / row['path']) == row['sha256']
public_path = ROOT / 'capability-evidence/diagnostics/af1080-final-ready/freeze.json'
public = read(public_path)
native = {r['name']: r['sha256'] for r in public['native62']}
managed = {Path(r['path']).name: r['sha256'] for r in public['configurations'][0]['payload']
           if Path(r['path']).parent == Path('.') and Path(r['path']).name.startswith('OcctSharp') and r['path'].endswith('.dll')}
assert len(managed) == 13
results = []
namesets = []
for mode in ['baseline', 'root']:
    for config in ['Release', 'Debug']:
        label = mode + '-' + config.lower() + '-1'
        build_path = DRAFT / ('build-managed-' + label + '-exit.json')
        receipt_path = DRAFT / ('managed-' + label + '-results.json')
        exit_path = DRAFT / ('managed-' + label + '-exit.json')
        build, receipt, terminal = read(build_path), read(receipt_path), read(exit_path)
        assert build['exitCode'] == 0 and build['sdk'] == '8.0.1-preview.85'
        folder = Path(build['output'])
        assert sha(folder / 'Cadoryx.ViewModels.dll') == build['viewModelsSha256'] == sha(Path(build['viewModels']))
        original = ROOT / ('Cadoryx.Tests/bin/' + config + '/net10.0')
        for p in folder.glob('*.dll'):
            if p.name in ['Cadoryx.ViewModels.dll', 'Cadoryx.Tests.dll']: continue
            assert sha(p) == sha(original / p.name)
        assert {p.name: sha(p) for p in folder.glob('OcctSharp*.dll')} == managed
        assert receipt['actualNativeModules'] == receipt['skipped'] == 0
        assert receipt['directCompiledAssertions'] and not receipt['fullXunitRunner']
        rows = receipt['results']; names = {r['name'] for r in rows}
        new = [r for r in rows if r['name'].startswith('Cadoryx.Tests.InspectionDocumentIdentityFeedbackTests.')]
        old = [r for r in rows if r not in new]
        failures = [r for r in rows if not r['passed']]
        assert len(rows) == len(names) == 185 and len(new) == 126 and len(old) == 59
        assert all(r['passed'] for r in old)
        assert len(failures) == (46 if mode == 'baseline' else 0)
        assert receipt['failed'] == len(failures) and receipt['passed'] == 185 - len(failures)
        assert terminal['exitCode'] == (1 if failures else 0)
        loaded = {r['name']: Path(r['path']) for r in receipt['assemblies']}
        assert loaded['Cadoryx.ViewModels'].parent == folder
        assert sha(loaded['Cadoryx.ViewModels']) == build['viewModelsSha256']
        namesets.append(names)
        results.append({'mode': mode, 'configuration': config, 'total': 185,
                        'passed': receipt['passed'], 'failed': receipt['failed'], 'original59Passed': True,
                        'actualNativeModules': 0, 'receipt': ref(receipt_path), 'build': ref(build_path), 'exit': ref(exit_path)})
assert all(n == namesets[0] for n in namesets)
for config in ['Release', 'Debug']:
    for prefix in ['tests', 'wpf']:
        terminal = DRAFT / (prefix + '-build-' + config.lower() + '-1-exit.json')
        assert read(terminal)['exitCode'] == 0
        log = terminal.with_name(prefix + '-build-' + config.lower() + '-1.log').read_text(encoding='utf-8-sig')
        assert '0 Warning(s)' in log and '0 Error(s)' in log
    app = ROOT / ('Cadoryx.wpf/bin/' + config + '/net10.0-windows/win-x64')
    tests = ROOT / ('Cadoryx.Tests/bin/' + config + '/net10.0')
    assert sha(app / 'Cadoryx.ViewModels.dll') == sha(tests / 'Cadoryx.ViewModels.dll')
    for folder in [app, tests]:
        assert {p.name: sha(p) for p in folder.glob('OcctSharp*.dll')} == managed
        assert {p.name: sha(p) for p in (folder / 'occt').glob('*.dll')} == native
record = {'recordedAt': datetime.now(timezone.utc).isoformat(),
          'state': 'F5_ROOT_PUBLIC85_BOTH185_COMPILED_ASSERTIONS_AND_FINAL_WPF_VERIFIED_NOT_FULL_XUNIT',
          'applied': ref(DRAFT / 'applied.json'), 'public85ByteBaseline': ref(public_path),
          'sourceFiles': [{'path': r['path'], 'sha256': sha(ROOT / r['path'])} for r in applied['files']],
          'configurations': results, 'actualRootManaged185Both': True,
          'rootTestsAndFinalWpfBothBuild0': True, 'finalWpfViewModelsMatchesTestedRoot': True,
          'public85SDK13Native62DiskMatchedBoth': True, 'originalSchemaAnd12ToolsKept': True,
          'runningOld6991Changed': False, 'D7Applied': False,
          'fullCurrentRootRegressionExecuted': False, 'realModelRerun': False,
          'actualDesktopRun': False, 'productionDevelopmentApplied': True, 'AFComplete': False}
OUT.mkdir(parents=True, exist_ok=True)
target = OUT / 'root-verification.json'; assert not target.exists()
target.write_bytes((json.dumps(record, indent=2) + '\n').encode('utf-8'))
print(json.dumps({'rootBoth185': True, 'public85BothFinalWpfBuild0': True,
                  'public85SDK13Native62DiskMatched': True, 'nativeRuntimeStarted': False,
                  'fullCurrentRootRegressionExecuted': False, 'productionDevelopmentApplied': True}))
