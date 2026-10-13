from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import shutil

ROOT = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent
DRAFT = ROOT / 'artifacts/af1081-local86-1'
def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()
def write(p, value):
    assert not p.exists(), p
    p.write_text(json.dumps(value, indent=2) + '\n', encoding='utf-8')
freeze = read(DRAFT / 'freeze.json')
old_draft = ROOT / 'artifacts/af1080-final-1'
old = read(old_draft / 'freeze.json')
perf = read(old_draft / 'performance-freeze.json')
for row in freeze['sourcePayload']: assert sha(Path(freeze['workspace']) / row['path']) == row['sha256']
sources = []
for row in old['originalModelPromptAndAssertionSources']:
    p = Path(freeze['workspace']) / 'tools/CadAgentWorkflowProbe' / Path(row['path']).name
    assert sha(p) == row['sha256']
    sources.append({'path': p.relative_to(ROOT).as_posix(), 'sha256': sha(p)})
for row in [perf['input'], perf['expectedImage'], perf['originalInstrumentedVerifier']]:
    assert sha(Path(row['path'])) == row['sha256']
assert Path(perf['input']['path']).stat().st_size == 88258425
assert perf['originalDirectRuns'] == 2 and perf['originalLifecycleCyclesPerRun'] == 20
assert perf['targetMs'] == 6482 and perf['directTimeoutSeconds'] == 1800
assert perf['productionOpenRuns'] == 2 and perf['productionOpenTimeoutSeconds'] == 900
configs = []
for c in freeze['configurations']:
    configs.append({'configuration': c['configuration'], 'candidate': c['candidate'],
                    'payload': c['appPayload'], 'native': freeze['native62']})
perf.update(recordedAt=datetime.now(timezone.utc).isoformat(),
            state='LOCAL86_SOURCE1081_PERFORMANCE_PREPARED_NOT_RUNTIME',
            sourceWorkspace=freeze['workspace'], sourcePayload=freeze['sourcePayload'],
            configurations=configs, publicSDK13Native62Match=False,
            localSDK13Native62Match=True, sharedProviderApplication16=freeze['sharedProvider16'],
            actualPerformanceRun=False, productionSourceApplied=False, publiclyPublished=False)
write(DRAFT / 'performance-freeze.json', perf)

original_provider = (ROOT / 'artifacts/run-af1080-final-provider-both-1.ps1').read_text(encoding='utf-8-sig')
provider = original_provider.replace('artifacts/af1080-final-1', 'artifacts/af1081-local86-1')
provider = provider.replace("if(!(Test-Path (Join-Path $taskDraft 'native-lane-released.json')))",
                            "if(!(Test-Path (Join-Path $taskRoot 'artifacts/local-sdk86-received-1/native-lane-released.json')))")
provider = provider.replace('capability-evidence/diagnostics/af1080-final-full/verification.json',
                            'capability-evidence/diagnostics/af1081-local86/full-verification.json')
provider = provider.replace('6815', '6830').replace('Frozen1080', 'Frozen1081')
provider = provider.replace('$taskProof.originalModelPromptAndAssertionSources', '$taskOriginalSources')
provider = provider.replace("$taskProvider=$taskProof.provider", "$taskOriginalSources=(Get-Content (Join-Path $taskDraft 'followup-preparation.json') -Raw|ConvertFrom-Json).originalModelPromptAndAssertionSources\n$taskProvider=$taskProof.provider")
provider = provider.replace('productionApplied=$true', 'productionApplied=$false;publiclyPublished=$false')
target = OUT / 'run-provider.ps1'
assert not target.exists()
target.write_text(provider, encoding='utf-8')

assertion = (ROOT / 'artifacts/assert-af1080-performance-ready-1.ps1').read_text(encoding='utf-8-sig')
assertion = assertion.replace('artifacts/af1080-final-1', 'artifacts/af1081-local86-1')
assertion = assertion.replace("if(!(Test-Path (Join-Path $taskDraft 'native-lane-released.json')))",
                              "if(!(Test-Path (Join-Path $taskRoot 'artifacts/local-sdk86-received-1/native-lane-released.json')))")
assertion = assertion.replace('AF1080_FINAL_WPF_BOTH_BUILD0_PUBLIC85_DISK_VERIFIED_NOT_RUNTIME',
                              'LOCAL86_SOURCE1081_PERFORMANCE_PREPARED_NOT_RUNTIME')
assertion = assertion.replace('capability-evidence/diagnostics/af1080-final-full/verification.json',
                              'capability-evidence/diagnostics/af1081-local86/full-verification.json')
assertion = assertion.replace('6815', '6830').replace('Final1080', 'Final1081')
(OUT / 'assert-performance-ready.ps1').write_text(assertion, encoding='utf-8')
for source_name, target_name, output_old, output_new in [
    ('run-af1080-direct-1.ps1', 'run-direct.ps1', 'artifacts/af1080-direct-1', 'artifacts/af1081-local86-direct-1'),
    ('run-af1080-production-open-1.ps1', 'run-production-open.ps1', 'artifacts/af1080-production-open-1', 'artifacts/af1081-local86-production-open-1')]:
    source = (ROOT / 'artifacts' / source_name).read_text(encoding='utf-8-sig')
    result = source.replace('artifacts/assert-af1080-performance-ready-1.ps1',
                            'capability-evidence/diagnostics/af1081-local86/assert-performance-ready.ps1').replace(output_old, output_new)
    assert result.replace('capability-evidence/diagnostics/af1081-local86/assert-performance-ready.ps1',
                           'artifacts/assert-af1080-performance-ready-1.ps1').replace(output_new, output_old) == source
    (OUT / target_name).write_text(result, encoding='utf-8')

host = DRAFT / 'window-observer-1'
host.mkdir(exist_ok=False)
for name in ['WindowHost.cs', 'WindowHost.csproj']:
    p = ROOT / 'tools/CadFiniteSurfaceWindowProbe' / name
    assert sha(p) == sha(old_draft / 'window-observer-1' / name)
    shutil.copyfile(p, host / name)
window_build = (ROOT / 'artifacts/build-af1080-window-observer-1.ps1').read_text(encoding='utf-8-sig')
window_build = window_build.replace('artifacts/af1080-final-1', 'artifacts/af1081-local86-1')
start = window_build.index('if(Test-Path $taskSource)')
end = window_build.index('$taskProof=')
window_build = window_build[:start] + window_build[end:]
(OUT / 'build-window.ps1').write_text(window_build, encoding='utf-8')
matrix = read(ROOT / 'capability-evidence/diagnostics/af1080-window-observer/verification.json')['matrix']
assert len(matrix) == 36
for m in matrix:
    assert sha(Path(m['fixture']['path'])) == m['fixture']['sha256']
    m['output'] = str(DRAFT / Path(m['output']).name)
write(DRAFT / 'followup-preparation.json', {
    'recordedAt': datetime.now(timezone.utc).isoformat(),
    'state': 'ORIGINAL_MODEL_PERFORMANCE_AND_D6_INPUTS_RETAINED_NOT_RUN',
    'originalModelPromptAndAssertionSources': sources, 'D6Matrix': matrix,
    'D6HostSource': {'path': str(host / 'WindowHost.cs'), 'sha256': sha(host / 'WindowHost.cs')},
    'fullRuntimePrerequisite': 'Both completed6830, original STEP24 only, no new failure or skip',
    'directBodyRetainedExceptIdentityAndOutputPaths': True,
    'productionOpenBodyRetainedExceptIdentityAndOutputPaths': True,
    'desktopInputStarted': False, 'nativeJobsStartedByPreparation': 0,
    'productionApplied': False, 'publiclyPublished': False, 'AFComplete': False})
print(json.dumps({'originalSources3': True, 'originalPerformanceBudgetsAndSample': True,
                  'originalD6HostAnd36Fixtures': True, 'followupRuntime': 'NOT RUN'}))
