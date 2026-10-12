from pathlib import Path
import datetime, hashlib, json

root = Path('C:/Users/yoiri/source/repos/Cadoryx')
draft = root / 'artifacts/d6-finite-newbody-draft-4'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref = lambda p: {'path': str(p), 'sha256': sha(p)}
build_path = draft / 'build-both-1-verification.json'
build = read(build_path)
workspace = Path(build['workspace'])
assert len(build['sourcePayload']) == 1073
assert build['original6556TestSourcesAndNamesPreserved']
assert build['serializationFieldsAndSectionVersionsUnchanged']
assert not build['newNativeBindingAdded'] and not build['publicUpstreamModified']
for row in build['sourcePayload']:
    assert sha(workspace / row['path']) == row['sha256']
recovery_path = root / 'capability-evidence/AF_PENDING_D6_FINITE_NEWBODY_RECOVERY.json'
recovery = read(recovery_path)
assert recovery['all1073ReplayVerified'] and len(recovery['sourceChanges']) == 11
assert recovery['original6556AndNew63TestsAndAssertionsUnchanged']
for key in ('build', 'patch', 'reviewDiff', 'recoveryBase'):
    assert sha(Path(recovery[key]['path'])) == recovery[key]['sha256']
normalization_path = root / 'capability-evidence/AF_PENDING_SOURCE1068_SDK31_DEPENDENCY_V2.json'
normalization = read(normalization_path)['feedNormalization']
assert set(normalization) == {'NuGet.Config'}
for row in build['sourcePayload']:
    assert sha(Path(recovery['replay']) / row['path']) == normalization.get(row['path'], {}).get('recoverySha256', row['sha256'])
prior_path = root / 'artifacts/d6-finite-newbody-draft-2/build-both-1-verification.json'
prior = read(prior_path)
prior_source = {r['path']: r['sha256'] for r in prior['sourcePayload']}
for row in build['sourcePayload']:
    if row['path'].startswith('Cadoryx.Tests/'):
        assert row['sha256'] == prior_source[row['path']]
matrix = []
failures = []
for configuration in ('Release', 'Debug'):
    cfg = build['configurations'][configuration]
    assert len(cfg['inventoryNames']) == 6619 and len(cfg['newNames']) == 63
    assert len(cfg['relatedNames']) == 99
    assert set(cfg['inventoryNames']) == set(prior['configurations'][configuration]['inventoryNames'])
    for row in cfg['testPayload']:
        assert sha(Path(cfg['testDirectory']) / row['path']) == row['sha256']
    for group, count in (('finite99', 99), ('agent189', 189)):
        path = draft / f'{group}-{configuration.lower()}-1-verification.json'
        verified = read(path)
        assert verified['count'] == verified['passed'] == count
        assert verified['failed'] == verified['skipped'] == 0 and verified['actualSdk31Native62']
        for key in ('build', 'terminal', 'trx', 'loadedNative'):
            assert sha(Path(verified[key]['path'])) == verified[key]['sha256']
        matrix.append({'configuration': configuration, 'group': group, 'passed': count, 'failed': 0,
                       'audit': ref(path), 'actualNative62': True, 'names': verified['names']})
    for folder in ('d6-finite-newbody-draft-2', 'f5-drawing-source-path-feedback-draft-2'):
        path = root / 'artifacts' / folder / f'agent189-{configuration.lower()}-1-verification.json'
        verified = read(path)
        assert verified['passed'] == 188 and verified['failed'] == 1
        assert 'RecoveryDirectiveRetainsAllTwelveToolsWithTheOriginalDefaultBudgets' in verified['failures'][0]['name']
        failures.append({'configuration': configuration, 'candidate': folder, 'audit': ref(path),
                         'failures': verified['failures']})
diagnostic = root / 'artifacts/f5-tool-budget-diagnostic-1'
budgets = {}
for iteration, label in ((2, 'before'), (3, 'after')):
    rows = [json.loads(s) for s in (diagnostic / f'runtime-{iteration}.log').read_text(encoding='utf-8-sig').splitlines() if s.startswith('{')]
    assert len(rows) == 2
    aggressive = next(r for r in rows if r['aggressive'])
    assert aggressive['availableToolTokens'] == 5597 and aggressive['maxOutput'] == 1024
    assert aggressive['originalBudgetsAndAlgorithmUnchanged']
    assert aggressive['totalToolCost'] == (5606 if label == 'before' else 5596)
    assert len(aggressive['fitted']) == (11 if label == 'before' else 12)
    budgets[label] = {'rows': rows, 'output': ref(diagnostic / f'runtime-{iteration}.log'),
                      'terminal': ref(diagnostic / f'runtime-{iteration}-exit.json')}
terminal = read(diagnostic / 'runtime-3-exit.json')
assert terminal['exitCode'] == 0 and terminal['algorithmUnchanged']
payload = Path(terminal['candidatePayload'])
for filename, key in (('Cadoryx.ViewModels.dll', 'viewModelsSha256'), ('Cadoryx.Agent.dll', 'agentSha256')):
    assert sha(payload / filename) == terminal[key]
native_path = root / 'capability-evidence/AF_PENDING_D6_NEWBODY_NATIVE_PREREQUISITE.json'
native = read(native_path)
assert len(native['records']) == 16 and native['rawFuse2RejectedNotProductApi']
for record in native['records']:
    assert sha(Path(record['terminal']['path'])) == record['terminal']['sha256']
for record in native['trackedDiagnosticSources']:
    assert sha(Path(record['path'])) == record['sha256']
out = root / 'capability-evidence/AF_PENDING_D6_FINITE_NEWBODY_FUNCTIONAL.json'
assert not out.exists()
out.write_text(json.dumps({
    'recordedAt': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'state': 'PASS_D6_FINAL_SOURCE1073_BOTH_FINITE99_AND_AGENT189_FULL_DESKTOP_PRODUCTION_SEPARATE',
    'candidate': str(workspace), 'build': ref(build_path), 'sourceCount': 1073,
    'inventoryCount': 6619, 'original6556Preserved': True, 'new63Preserved': True,
    'originalAndNewTestSourcesUnchangedFromDraft2': True,
    'functionalMatrix': matrix, 'recovery': ref(recovery_path), 'whole1073ReplayRechecked': True,
    'existingFeedNormalization': ref(normalization_path),
    'initialAuditFailure': 'First aggregate audit compared normalized NuGet.Config directly to the original absolute feed hash; it stopped before writing. Corrected only the audit to use the existing V2 normalization; no candidate or runtime changed.',
    'nativePrerequisite': ref(native_path), 'nativePrerequisiteRecords': 16,
    'priorNativeFailedDiagnosticsRetained': native['priorFailedDiagnosticsRetained'],
    'priorAgentRecoveryFailuresRetained': failures, 'actualToolBudgetDiagnostic': budgets,
    'toolAlgorithmAndBudgetsUnchanged': True, 'shortRecoveryAggressiveMarginTokens': 1,
    'arbitraryLongConversationsTwelveToolsGuaranteed': False,
    'capability': 'cadoryx.finite-surface-new-body.1',
    'documentSection': 60, 'featuresSection': 43, 'newCapabilityCreator': '0.7.11',
    'applicationDebugUsesPackageReleaseNative': True,
    'prerequisiteDebugUsesRealDebugToolkit': True,
    'supportedLimits': ['plane', 'sphere', 'cylinder'], 'start': 'explicit planar rigid frame',
    'nativeFailureFallback': False, 'newNativeBindingsAdded': False,
    'applicationExportGuardChanged': False, 'publicUpstreamModified': False,
    'full6619': 'NOT_RUN', 'actualWpfWindows': 'NOT_RUN', 'provider': 'NOT_RUN',
    'performanceAcceptance': False, 'productionApplied': False, 'productionSourceCount': 1000,
    'productionSdk': 26, 'cleanup': False, 'desktopInputStarted': False,
    'AF': 'ACTIVE', 'G': 'USER_DEFERRED'
}, indent=2) + '\n', encoding='utf-8')
print('PASS final source1073, replay1073, original6556/new63, finite99 and agent189 both; immutable functional evidence saved.')
