from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent
DRAFT = ROOT / 'artifacts/af1081-attachment-1'
def read(path): return json.loads(path.read_text(encoding='utf-8-sig'))
def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest().upper()
def identity(row): return (row['typeName'], row['methodName'], json.dumps(row['args'], sort_keys=True))
baseline = read(DRAFT / 'baseline-result-final.json')
candidate = read(DRAFT / 'candidate-result-final.json')
assert len(baseline['rows']) == len(candidate['rows']) == 446
configs = []
for config in ['Release', 'Debug']:
    old = [r for r in baseline['rows'] if r['config'] == config]
    new = [r for r in candidate['rows'] if r['config'] == config]
    assert len(old) == len(new) == 223
    assert {identity(r) for r in old} == {identity(r) for r in new}
    originals = [r for r in new if r['typeName'] != 'MainWindowAttachmentFailureTests']
    assert len(originals) == 208 and all(r['outcome'] == 'Passed' for r in originals)
    assert all(r['outcome'] == 'Passed' for r in new)
    rejected = [r for r in old if r['outcome'] == 'Failed']
    assert len(rejected) == 15 and all(r['typeName'] == 'MainWindowAttachmentFailureTests' for r in rejected)
    actual = next(r for r in candidate['configurations'] if r['config'] == config)
    assert not actual['nativeModules']
    for assembly in actual['assemblies']:
        assert sha(Path(assembly['location'])) == assembly['sha256']
    vm = next(r for r in actual['assemblies'] if r['name'] == 'Cadoryx.ViewModels')
    wpf = ROOT / f'Cadoryx.wpf/bin/{config}/net10.0-windows/win-x64'
    assert sha(wpf / 'Cadoryx.ViewModels.dll') == vm['sha256']
    native = read(ROOT / 'artifacts/af1080-final-1/freeze.json')['native62']
    assert len(native) == 62
    for row in native:
        assert sha(wpf / 'occt' / row['name']) == row['sha256'], row['name']
    build_logs = [ROOT / f'artifacts/af1081-attachment-candidate-build-{config.lower()}-{3 if config == "Release" else 2}.log',
                  ROOT / f'artifacts/af1081-attachment-wpf-build-{config.lower()}-1.log']
    for path in build_logs:
        text = path.read_text(encoding='utf-8-sig')
        assert 'Build succeeded.' in text and '0 Warning(s)' in text and '0 Error(s)' in text
    configs.append({'configuration': config, 'originalAssertions': 208, 'newFailureCases': 15,
                    'oldVersionPassed': 208, 'oldVersionFailed': 15, 'candidatePassed': 223,
                    'nativeModulesLoaded': 0, 'actualWpfViewModelsMatched': True,
                    'diskNative62StillPublic85': True, 'buildLogs': [{'path': str(p), 'sha256': sha(p)} for p in build_logs],
                    'candidateAssemblies': actual['assemblies'], 'originalFailureSummaries': [
                        {'method': r['methodName'], 'args': r['args'], 'error': r['exception'].splitlines()[0]} for r in rejected]})
frozen = read(ROOT / 'artifacts/af1080-final-1/freeze.json')
changes = []
for row in frozen['sourcePayload']:
    path = ROOT / row['path']
    if sha(path) != row['sha256']: changes.append(row['path'])
assert changes == ['Cadoryx.ViewModels/MainWindowViewModel.cs']
evidence = [DRAFT / name for name in ['baseline-result.json', 'baseline-result-2.json', 'baseline-result-3.json',
           'baseline-result-final.json', 'candidate-result-1.json', 'candidate-result-2.json', 'candidate-result-final.json']]
result = {'recordedAt': datetime.now(timezone.utc).isoformat(),
          'state': 'AF1081_MAIN_WINDOW_ATTACHMENT_RECOVERY_MANAGED_PRODUCTION_VERIFIED',
          'configurations': configs, 'productionSource': 1081, 'productionPackage': '8.0.1-preview.85',
          'sourceInputsExpected': 1193, 'oldInputsUnchangedExceptMainWindow': 1191,
          'sourceChanges': [{'path': str(p), 'sha256': sha(p)} for p in [
              ROOT / 'Cadoryx.ViewModels/MainWindowViewModel.cs', ROOT / 'Cadoryx.Tests/MainWindowAttachmentFailureTests.cs']],
          'expectedFullTestCount': 6830, 'actualFullDiscovery': False, 'actualFullRuntime': False,
          'compiledAssertionInvocationNotFullXunit': True,
          'exchangeFixture': 'Native serialized empty author behind an importer stub; importer dispatch/lifetime only, not STEP geometry acceptance',
          'fixtureFailuresPreserved': 'Missing Anchorables, retained asset without extension, initial compile namespaces corrected; original failures retained',
          'excludedCandidateResult1': 'First candidate compile failed at an old New() call; subsequent probe read old binaries, not accepted as candidate validation',
          'evidence': [{'path': str(p), 'sha256': sha(p)} for p in evidence],
          'full1080PreparationIsHistorical': True, 'nextFinalPayloadRequires1081SourceAndSelectedSdk': True,
          'nativeGeometry': 'NOT RUN', 'modelWorkflow': 'NOT RUN', 'physicalDesktop': 'NOT RUN',
          'AF': 'ACTIVE', 'G': 'USER_DEFERRED', 'cleanup': False}
target = OUT / 'verification.json'; assert not target.exists()
target.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'state': result['state'], 'each': 223, 'oldFailuresEach': 15, 'originalSourcesChanged': changes,
                  'expectedFullCount': 6830, 'fullRuntime': False}))
