from pathlib import Path
from datetime import datetime, timezone
import hashlib, json, shutil

root = Path(__file__).resolve().parents[1]
draft = root / 'artifacts/af1080-final-1'
dest = root / 'capability-evidence/diagnostics/af1080-final-ready'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref = lambda p: {'path': p.relative_to(root).as_posix(), 'sha256': sha(p)}
assert not dest.exists()
prep = read(draft / 'preparation.json')
full = read(draft / 'freeze.json')
performance = read(draft / 'performance-freeze.json')
managed = read(draft / 'final-app-managed-result.json')
runners = read(draft / 'runners-verification.json')
window = read(root / 'capability-evidence/diagnostics/af1080-window-observer/verification.json')
api = read(draft / 'occt-api-usage.json')
oldApiPath = root / 'capability-evidence/diagnostics/public-nuget-migration/source1073-occt-usage.json'
oldApi = read(oldApiPath)
assert api['assemblyCount'] == len(api['assemblies']) == 76
assert api['typeCount'] == len(api['types']) == 221 and api['memberCount'] == len(api['members']) == 696
assert not api['directNativeImports'] and not api['sdkAssembliesLoadedOrNativeCodeExecuted']
assert {r['symbol'] for r in api['types']} == {r['symbol'] for r in oldApi['types']}
assert {r['symbol'] for r in api['members']} == {r['symbol'] for r in oldApi['members']}
for r in api['assemblies']:
    assert sha(Path(r['path'])) == r['sha256']
assert prep['payload'] == full['sourcePayload'] == performance['sourcePayload']
assert len(prep['payload']) == window['productionSourceInputsUnchanged'] == 1192
for r in prep['payload']:
    assert sha(root / r['path']) == sha(Path(prep['workspace']) / r['path']) == r['sha256']
for c in full['configurations']:
    for r in c['payload']:
        assert sha(Path(c['testDirectory']) / r['path']) == r['sha256']
for c in performance['configurations']:
    for r in c['payload']:
        assert sha(Path(c['candidate']) / r['path']) == r['sha256']
for r in full['providerPayload']:
    assert sha(Path(full['provider']) / r['path']) == r['sha256']
assert len(full['sharedProviderApplication16']) == 16
assert len(managed['rows']) == 416 and all(r['outcome'] == 'Passed' for r in managed['rows'])
for c in managed['configurations']:
    assert not c['nativeModules'] and len([r for r in managed['rows'] if r['config'] == c['config']]) == 208
    for a in c['assemblies']:
        assert sha(Path(a['location'])) == a['sha256']
assert full['expectedTestCount'] == 6815 and not full['actualFullDiscovery'] and not full['actualFullRuntime']
assert not performance['actualPerformanceRun'] and not window['actualDesktopRun']
assert window['originalRequireCallsRetained'] == 29 and window['originalEvidenceAddCallsRetained'] == 5
assert window['originalSixStatesAnd30And300SecondsRetained'] and len(window['matrix']) == 36
assert runners['directRuntimeBodyUnchanged'] and runners['productionOpenRuntimeBodyUnchanged'] and runners['nativeLaneHandoffMandatory']
for s in runners['scripts']:
    assert sha(root / s['path']) == s['sha256']
assert not (draft / 'native-lane-released.json').exists()
assert not (draft / 'full-both-exit.json').exists() and not (draft / 'provider-runtime-freeze.json').exists()
stages = read(draft / 'build-exit.json')['stages']
assert len(stages) == 5 and all(s['exitCode'] == 0 for s in stages)
dest.mkdir(parents=True)
names = ['preparation.json', 'freeze.json', 'performance-freeze.json', 'final-app-managed-result.json', 'occt-api-usage.json',
         'runners-verification.json', 'build-exit.json', 'restore.log', 'release.log', 'debug.log', 'provider-restore.log', 'provider-publish.log']
for n in names:
    shutil.copyfile(draft / n, dest / n)
for s in runners['scripts']:
    shutil.copyfile(root / s['path'], dest / Path(s['path']).name)
shutil.copyfile(Path(__file__), dest / 'audit.py')
report = {'recordedAt': datetime.now(timezone.utc).isoformat(), 'state': 'AF1080_FINAL_SOURCE_APP_PROVIDER_PERFORMANCE_WINDOW_PREPARED_NOT_NATIVE_RUNTIME',
          'baseHead': prep['baseHead'], 'sourceInputs': 1192, 'fullSolutionReleaseAndDebugBuild0': True,
          'actualFinalWpfCompiledAssertionsEach': 208, 'directAssertionInvocationNotFullXunit': True,
          'providerApplicationShared16ByteIdentical': True, 'publicSDK13Native62DiskMatched': True,
          'originalModelPromptAndAssertionSources': full['originalModelPromptAndAssertionSources'],
          'metadataAudit': ref(dest / 'occt-api-usage.json'), 'metadataAuditSummary': '76 assemblies;221 OCCT types;696 members;0 direct native imports',
          'occtType221AndMember696SetsMatchOriginal1073': True, 'occtReferenceBaseline': ref(oldApiPath),
          'expectedFullTestCount': 6815, 'actualFullDiscovery': False, 'actualFullRuntime': False, 'actualModelRun': False,
          'originalDirectRuns': 2, 'originalCycles': 20, 'targetMs': 6482, 'directTimeoutSeconds': 1800,
          'originalProductionOpenRuns': 2, 'productionOpenTimeoutSeconds': 900, 'fullImageSize': [788, 411],
          'actualPerformanceRun': False, 'D6Original36Matrix': True, 'D6Original29RequireCalls': True,
          'D6OriginalSixStatesAnd30And300Seconds': True, 'actualDesktopRun': False,
          'nativeLane': 'OcctSharp still owns lane; final package and explicit handoff not yet received',
          'runnersDoNotLaunchWithoutNativeLaneHandoff': True,
          'public85FrozenPreparationDoesNotProveFutureLocal86Integration': True,
          'productionSourceApplied': True, 'productionDialogChanged': False, 'nativeJobsStarted': 0,
          'evidence': [ref(dest / n) for n in names], 'AFComplete': False, 'G': 'USER_DEFERRED', 'cleanup': False}
(dest / 'verification.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'finalApp208Each': True, 'provider16Exact': True, 'full6815': 'NOT RUN', 'D6': '36 NOT RUN', 'performance': 'NOT RUN', 'nativeJobs': 0}))
