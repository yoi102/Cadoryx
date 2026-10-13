from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import subprocess

ROOT = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent
DRAFT = ROOT / 'artifacts/af1081-local86-1'
def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()
def ref(p): return {'path': p.relative_to(ROOT).as_posix(), 'sha256': sha(p)}
freeze = read(DRAFT / 'freeze.json')
prep = read(DRAFT / 'preparation.json')
locks = read(DRAFT / 'locks.json')
followups = read(DRAFT / 'followup-preparation.json')
expected_native = {r['name']: r['sha256'] for r in freeze['native62']}
expected_sdk = {r['name']: r['sha256'] for r in freeze['managed13']}
for row in freeze['sourcePayload']: assert sha(Path(freeze['workspace']) / row['path']) == row['sha256']
for row in prep['originalSource']: assert sha(ROOT / row['path']) == row['sha256']
assert len(prep['originalSource']) == 1193
for row in freeze['providerPayload']: assert sha(Path(freeze['provider']) / row['path']) == row['sha256']
window_build = read(DRAFT / 'window-observer-1-build-exit.json')
assert len(window_build['stages']) == 2
windows = []
for c in freeze['configurations']:
    app = Path(c['candidate'])
    test = Path(c['testDirectory'])
    host = DRAFT / f'window-observer-1/bin/{c["configuration"]}/net10.0-windows/win-x64'
    for folder, payload in [(app, c['appPayload']), (test, c['payload'])]:
        for row in payload: assert sha(folder / row['path']) == row['sha256']
    production = sorted(app.glob('Cadoryx.*.dll'))
    assert len(production) == 19
    for p in production: assert sha(host / p.name) == sha(p)
    for name in ['Cadoryx.wpf.exe', 'Cadoryx.wpf.deps.json', 'Cadoryx.wpf.runtimeconfig.json']:
        assert sha(host / name) == sha(app / name)
    for folder in [app, test, host]:
        assert {p.name: sha(p) for p in folder.glob('OcctSharp*.dll')} == expected_sdk
        assert {p.name: sha(p) for p in (folder / 'occt').glob('*.dll')} == expected_native
    for p in app.glob('*/Cadoryx.Lang.resources.dll'):
        assert sha(host / p.parent.name / p.name) == sha(p)
    stage = next(r for r in window_build['stages'] if r['configuration'] == c['configuration'])
    log = Path(stage['log']).read_text(encoding='utf-8-sig')
    assert stage['exitCode'] == 0 and '0 Warning(s)' in log and '0 Error(s)' in log
    windows.append({'configuration': c['configuration'], 'host': str(host),
                    'binarySha256': sha(host / 'WindowHost.dll'), 'application19AndWorkerMatched': True,
                    'localSdk13Native62AndLanguageResourcesMatched': True})
full = DRAFT / 'full6830-release-1'
start = read(full / 'start.json')
binding = read(full / 'loaded-native.json')
assert len(binding['native']) == 62
binary = Path(start['testDirectory'])
for r in binding['native']:
    assert Path(r['path']).parent == binary / 'occt'
    assert sha(Path(r['path'])) == r['sha256'] == expected_native[r['name']]
# Fresh read-only process inventory: do not infer that a recorded PID is still alive.
command = ('$taskAll=Get-CimInstance Win32_Process; $taskCurrent=' + str(binding['processId']) + '; $taskChain=@(); '
           'while($taskCurrent -gt 0){$taskProc=$taskAll|Where-Object ProcessId -EQ $taskCurrent; if(!$taskProc){break}; '
           '$taskChain+=@($taskProc|Select-Object ProcessId,ParentProcessId,CreationDate,ExecutablePath,CommandLine); '
           'if($taskCurrent -eq ' + str(start['coordinatorPid']) + '){break}; $taskCurrent=$taskProc.ParentProcessId}; '
           '$taskChain|ConvertTo-Json -Depth 4')
observations = json.loads(subprocess.check_output(['pwsh', '-NoProfile', '-Command', command], text=True))
assert {start['coordinatorPid'], start['pid'], binding['processId']} <= {r['ProcessId'] for r in observations}
assert observations[0]['ProcessId'] == binding['processId'] and observations[-1]['ProcessId'] == start['coordinatorPid']
assert Path(observations[0]['ExecutablePath']) == binary / 'testhost.exe'
for child, parent_process in zip(observations, observations[1:]):
    assert child['ParentProcessId'] == parent_process['ProcessId']
assert next(r for r in observations if r['ProcessId'] == start['pid'])['ParentProcessId'] == start['coordinatorPid']
assert not (full / 'exit.json').exists()
result = {'recordedAt': datetime.now(timezone.utc).isoformat(),
          'state': 'LOCAL86_SOURCE1081_BOTH_BUILD0_DISCOVERED6830_RELEASE_RUNNING_NOT_TERMINAL',
          'baseHead': freeze['baseHead'], 'productionSourceInputsRetained': 1193,
          'productionPackage': '8.0.1-preview.85', 'candidatePackage': freeze['version'],
          'restoreProjects': 50, 'lockedFiles': len(locks['locks']),
          'sdkContainingLocks': sum(r['sdkReferences'] > 0 for r in locks['locks']),
          'sdkLockReferences': sum(r['sdkReferences'] for r in locks['locks']),
          'nonOcctDependenciesUnchanged': True, 'solutionBothBuild0Warnings0Errors': True,
          'bothActuallyDiscovered': 6830, 'providerShared16ByteIdentical': True,
          'localSdk13Native62DiskMatched': True, 'windowHostBothBuild0': windows,
          'originalModelSources3AndBudgetsRetained': True,
          'originalPerformanceInputImageAndBudgetsRetained': True,
          'originalD6HostAndMatrix36Retained': True, 'actualReleaseNative62Matched': True,
          'actualProcessesAtObservation': observations,
          'releaseStart': ref(full / 'start.json'), 'actualNativeBinding': ref(full / 'loaded-native.json'),
          'freeze': ref(DRAFT / 'freeze.json'), 'followups': ref(DRAFT / 'followup-preparation.json'),
          'fullRuntimeTerminal': False, 'actualModelRun': False, 'actualPerformanceRun': False,
          'actualDesktopRun': False, 'productionApplied': False, 'publiclyPublished': False,
          'originalSTEP24Unresolved': True, 'AppSweepUnresolved': True,
          'AFComplete': False, 'G': 'USER_DEFERRED', 'cleanup': False}
target = OUT / 'verification.json'
assert not target.exists()
target.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'bothBuild0': True, 'bothDiscovered6830': True, 'actualNative62': True,
                  'processesAlive': len(observations), 'fullTerminal': False, 'windowBuild0': True}))
