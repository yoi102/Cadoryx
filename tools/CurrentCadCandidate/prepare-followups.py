from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json

ROOT = Path(__file__).resolve().parents[2]
DRAFT = ROOT / 'artifacts/af1082-f5-d7-local87cu1-1'
def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()
def ref(p): return {'path': p.relative_to(ROOT).as_posix(), 'sha256': sha(p)}
def write(p, data):
    assert not p.exists()
    p.write_bytes((json.dumps(data, indent=2) + '\n').encode('utf-8'))

freeze_path = DRAFT / 'freeze.json'; freeze = read(freeze_path)
old = read(ROOT / 'artifacts/af1081-local86-1/followup-preparation.json')
perf = read(ROOT / 'artifacts/af1081-local86-1/performance-freeze.json')
for row in freeze['sourcePayload']: assert sha(Path(freeze['workspace']) / row['path']) == row['sha256']
sources = []
for row in old['originalModelPromptAndAssertionSources']:
    original = ROOT / row['path']; assert sha(original) == row['sha256']
    current = Path(freeze['workspace']) / 'tools/CadAgentWorkflowProbe' / original.name
    assert sha(current) == row['sha256']; sources.append(ref(current))
assert len(sources) == 3
for key in ['input', 'expectedImage', 'originalInstrumentedVerifier']:
    assert sha(Path(perf[key]['path'])) == perf[key]['sha256']
assert Path(perf['input']['path']).stat().st_size == 88258425
assert perf['originalDirectRuns'] == 2 and perf['originalLifecycleCyclesPerRun'] == 20
assert perf['targetMs'] == 6482 and perf['directTimeoutSeconds'] == 1800
assert perf['productionOpenRuns'] == 2 and perf['productionOpenTimeoutSeconds'] == 900
configs = [{'configuration': c['configuration'], 'candidate': c['candidate'],
            'payload': c['appPayload'], 'native': freeze['native62']} for c in freeze['configurations']]
perf.update(recordedAt=datetime.now(timezone.utc).isoformat(),
            state='CURRENT_F5_D7_EXACT87CU1_PERFORMANCE_PREPARED_NOT_RUNTIME',
            sourceWorkspace=freeze['workspace'], sourcePayload=freeze['sourcePayload'],
            configurations=configs, localSDK13Native62Match=True, publicSDK13Native62Match=False,
            sharedProviderApplication16=freeze['sharedProvider16'], actualPerformanceRun=False,
            productionSourceApplied=False, publiclyPublished=False)
performance_path = DRAFT / 'performance-freeze.json'
if performance_path.exists():
    received = read(performance_path)
    assert {k: v for k, v in received.items() if k != 'recordedAt'} == {k: v for k, v in perf.items() if k != 'recordedAt'}
else:
    write(performance_path, perf)
matrix = old['D6Matrix']; assert len(matrix) == 36
for row in matrix:
    assert sha(Path(row['fixture']['path'])) == row['fixture']['sha256']
    row['output'] = str(DRAFT / 'windows' / Path(row['output']).name)
recovery = read(ROOT / 'artifacts/af1081-local86-1/recovery-preparation.json')
grid = recovery['original36Matrix']
assert recovery['original24'] == 24 and grid['planned'] == 36
assert len(grid['cultures']) * len(grid['nativeLosses']) * len(grid['routes']) * len(grid['failedActivePane']) == 36
assert grid['readinessDeadlineSeconds'] == 30
write(DRAFT / 'recovery-preparation.json', recovery)
record = {'recordedAt': datetime.now(timezone.utc).isoformat(),
          'state': 'CURRENT_F5_D7_ORIGINAL_MODEL_WINDOW_AND_PERFORMANCE_INPUTS_RETAINED_NOT_RUN',
          'freeze': ref(freeze_path), 'originalModelPromptAndAssertionSources': sources,
          'D6Matrix': matrix, 'D6HostSource': ref(ROOT / 'tools/CadFiniteSurfaceWindowProbe/WindowHost.cs'),
          'recoveryHosts': ref(ROOT / 'capability-evidence/diagnostics/af1082-f5-d7-local87cu1/hosts-verification.json'),
          'original24And36AndD6BudgetsChanged': False,
          'fullRuntimePrerequisite': 'Complete both7117 TRX; no new failures, exact STEP24 only; no skipped cases',
          'sdkRuntimeLanePrerequisite': 'Explicit returned native lane and actual idle observation',
          'originalModel8192Context16ToolRoundsFourMinutesChanged': False,
          'performanceOverlapPermitted': False,
          'nativeRuntimeStarted': False, 'desktopInputStarted': False, 'AFComplete': False}
write(DRAFT / 'followup-preparation.json', record)
print(json.dumps({'originalModelSources': 3, 'D6ExactMatrix': 36,
                  'recoveryOriginal24And36': True, 'MiniPCBudgetsAndWholeImageKept': True, 'runtime': 'NOT RUN'}))
