from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json

ROOT = Path(__file__).resolve().parents[2]
TOOL = Path(__file__).resolve().parent
DRAFT = ROOT / 'artifacts/inspection-document-id-draft-1'
OUT = ROOT / 'capability-evidence/diagnostics/inspection-document-id'
def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()
def ref(p): return {'path': p.relative_to(ROOT).as_posix(), 'sha256': sha(p)}

parent_path = ROOT / 'artifacts/af1082-d7-local87cu1-1/freeze.json'
parent = read(parent_path)
source = DRAFT / 'workspace'
changed = 'Cadoryx.ViewModels/Toolboxes/CadAgentDocumentTools.cs'
for row in parent['sourcePayload']:
    if row['path'] != changed: assert sha(source / row['path']) == row['sha256']
helper = source / 'Cadoryx.ViewModels/Toolboxes/CadInspectionDocumentIdentityFeedback.cs'
assert sha(helper) == sha(TOOL / helper.name)
results = []
sets = {}
for mode in ['baseline', 'candidate']:
    for config in ['Release', 'Debug']:
        label = mode + '-' + config.lower() + '-6'
        build_path = DRAFT / ('build-managed-' + label + '-exit.json')
        build = read(build_path)
        assert build['exitCode'] == 0
        binary = Path(build['output'])
        assert sha(binary / 'Cadoryx.ViewModels.dll') == sha(Path(build['viewModels'])) == build['viewModelsSha256']
        config_row = next(c for c in parent['configurations'] if c['configuration'] == config)
        original_binary = Path(config_row['testDirectory'])
        for p in binary.glob('*.dll'):
            if p.name in ['Cadoryx.ViewModels.dll', 'Cadoryx.Tests.dll']: continue
            assert sha(p) == sha(original_binary / p.name), p.name
        log = (DRAFT / ('build-managed-' + label + '.log')).read_text(encoding='utf-8-sig')
        assert '0 Warning(s)' in log and '0 Error(s)' in log
        receipt_path = DRAFT / ('managed-' + label + '-results.json')
        exit_path = DRAFT / ('managed-' + label + '-exit.json')
        receipt, terminal = read(receipt_path), read(exit_path)
        assert receipt['directCompiledAssertions'] and not receipt['fullXunitRunner']
        assert receipt['actualNativeModules'] == receipt['skipped'] == 0
        names = {r['name'] for r in receipt['results']}
        assert len(names) == len(receipt['results']) == 184
        new = [r for r in receipt['results'] if r['name'].startswith('Cadoryx.Tests.InspectionDocumentIdentityFeedbackTests.')]
        old = [r for r in receipt['results'] if r not in new]
        assert len(new) == 125 and len(old) == 59 and all(r['passed'] for r in old)
        failed = [r for r in receipt['results'] if not r['passed']]
        assert receipt['passed'] == 184 - len(failed) and receipt['failed'] == len(failed)
        assert len(failed) == (46 if mode == 'baseline' else 0)
        assert terminal['exitCode'] == (1 if failed else 0)
        loaded = {r['name']: Path(r['path']) for r in receipt['assemblies']}
        assert loaded['Cadoryx.ViewModels'].parent == binary
        assert sha(loaded['Cadoryx.ViewModels']) == build['viewModelsSha256']
        for name, path in loaded.items():
            if name.startswith(('Cadoryx.', 'OcctSharp')):
                assert path.parent == binary and sha(path) == sha(binary / path.name)
        sets[(mode, config)] = names
        results.append({'mode': mode, 'configuration': config, 'total': 184,
                        'passed': receipt['passed'], 'failed': receipt['failed'], 'new125': len(new),
                        'original59Passed': True, 'actualNativeModules': 0,
                        'build': ref(build_path), 'receipt': ref(receipt_path), 'exit': ref(exit_path),
                        'failures': failed})
assert all(names == sets[('baseline', 'Release')] for names in sets.values())
for config in ['Release', 'Debug']:
    build = DRAFT / ('build-viewmodels-' + config.lower() + '-2-exit.json')
    assert read(build)['exitCode'] == 0
    log = build.with_name('build-viewmodels-' + config.lower() + '-2.log').read_text(encoding='utf-8-sig')
    assert '0 Warning(s)' in log and '0 Error(s)' in log
record = {'recordedAt': datetime.now(timezone.utc).isoformat(),
          'state': 'F5_ALL18_DOCUMENT_FILTER_ROLES_BOTH_MANAGED184_VERIFIED_NOT_MODEL_OR_FULL_XUNIT',
          'parent': ref(parent_path), 'source': ref(source / changed), 'helper': ref(helper),
          'tests': ref(TOOL / 'InspectionDocumentIdentityFeedbackTests.cs'),
          'toolSource': [ref(p) for p in sorted(TOOL.iterdir()) if p.is_file()],
          'configurations': results, 'same184NamesBothModesAndConfigurations': True,
          'original95AssertionsRetained': True, 'newLifecycleClosingRequested': 1,
          'newTypedIdentityEmptyPageGuards': 29,
          'firstFourFixtureRuns': '59 pass/95 invalid parameter-name fixture failures, retained; not product baseline',
          'run5': 'Each candidate183/1; assembly fixture used wrong instances JSON path; retained',
          'nativeOrPerformanceWorkStarted': False, 'whole6991RegressionChanged': False,
          'toolCountOrSchemaChanged': False, 'realProviderRerun': False,
          'productionApplied': False, 'AFComplete': False}
OUT.mkdir(parents=True, exist_ok=True)
target = OUT / 'verification.json'
assert not target.exists()
target.write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'baselineBoth': {'passed': 138, 'failed': 46},
                  'candidateBoth': {'passed': 184, 'failed': 0}, 'actualNative': 0,
                  'original59': True, 'full6991Changed': False, 'productionApplied': False}))
