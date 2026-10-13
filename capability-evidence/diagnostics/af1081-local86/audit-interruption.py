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
observed = read(DRAFT / 'debug-interrupted-observation-1.json')
assert observed['state'] == 'DEBUG_AND_QUEUE_PROCESSES_ABSENT_WITHOUT_TERMINAL'
assert not observed['observedRecordedProcesses'] and observed['actualCadConsumersIdle']
assert not observed['debugTrxExists'] and not observed['debugExitExists']
old = DRAFT / 'full6830-debug-1'
for row in observed['files']:
    assert sha(old / row['name']) == row['sha256']
assert not (old / 'exit.json').exists() and not (old / 'runtime.trx').exists()
freeze = read(DRAFT / 'freeze.json')
for row in freeze['sourcePayload']: assert sha(Path(freeze['workspace']) / row['path']) == row['sha256']
for c in freeze['configurations']:
    for row in c['payload']: assert sha(Path(c['testDirectory']) / row['path']) == row['sha256']
release_path = OUT / 'release-full-verification.json'
release = read(release_path)['configurations'][0]
assert release['total'] == 6830 and release['passed'] == 6806 and release['failed'] == 24 and release['skipped'] == 0
assert not release['newFailureNames'] and release['actualNative62Matched']
for key in ['trx', 'binding', 'exit']: assert sha(ROOT / release[key]['path']) == release[key]['sha256']
handoff = read(DRAFT / 'native-lane-to-sdk-1.json')
assert handoff['actualCadConsumersIdle'] and not handoff['cadoryxRuntimeQueued']
text = (old / 'stdout.log').read_text(encoding='utf-8-sig')
failed = {line[9:line.rfind(' [')] for line in text.splitlines() if line.startswith('  Failed Cadoryx.')}
known = {r['name'] for r in release['failures']}
result = {'recordedAt': datetime.now(timezone.utc).isoformat(),
          'state': 'ORIGINAL_DEBUG_INTERRUPTION_SOURCE_BINARY_RELEASE_PRESERVED_NOT_TEST_TERMINAL',
          'interruption': ref(DRAFT / 'debug-interrupted-observation-1.json'),
          'nativeHandoff': ref(DRAFT / 'native-lane-to-sdk-1.json'),
          'releaseCompletedAudit': ref(release_path), 'freeze': ref(DRAFT / 'freeze.json'),
          'sourceAndBothTestPayloadsUnchanged': True, 'releaseRerun': False,
          'debugOriginalTrxAndExit': 'MISSING', 'debugCause': 'UNKNOWN',
          'loggedPassRowsNotTrxResults': sum(line.startswith('  Passed Cadoryx.') for line in text.splitlines()),
          'loggedFailedNamesNotCompleteResults': sorted(failed), 'newLoggedFailureNames': sorted(failed - known),
          'debugPassed': False, 'sdkNativeLaneExplicitlyGranted': True, 'resumeOnlyMissingDebug': True,
          'actualModelRun': False, 'actualPerformanceRun': False, 'actualDesktopRun': False,
          'productionApplied': False, 'publiclyPublished': False, 'AFComplete': False, 'G': 'USER_DEFERRED'}
target = OUT / 'debug-interruption-verification.json'
assert not target.exists()
target.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
print(json.dumps({'releasePreserved': True, 'frozenInputsAndBinaries': True, 'debugTerminal': False,
                  'originalLoggedFailureNames': len(failed & known), 'newLoggedFailureNames': len(failed - known)}))
