"""Record the user's exact STEP failure deferral without changing test outcomes."""
from datetime import datetime, timezone
from pathlib import Path
import hashlib
import json

ROOT = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent

def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))

def ref(path):
    return {'path': path.relative_to(ROOT).as_posix(),
            'sha256': hashlib.sha256(path.read_bytes()).hexdigest().upper()}

release_path = OUT / 'release-full-verification.json'
parent_path = ROOT / 'capability-evidence/diagnostics/af1076-root-full/verification.json'
release = read(release_path)
parent = read(parent_path)
actual = release['configurations'][0]
assert actual['configuration'] == 'Release'
assert (actual['total'], actual['passed'], actual['failed'], actual['skipped']) == (6830, 6806, 24, 0)
assert actual['newFailureNames'] == [] and actual['actualNative62Matched']
names = sorted(row['name'] for row in actual['failures'])
assert len(names) == len(set(names)) == 24
for config in parent['configurations']:
    assert sorted(row['name'] for row in config['failures']) == names

target = OUT / 'step24-user-deferral.json'
assert not target.exists(), 'Preserve the original deferral receipt.'
result = {
    'recordedAt': datetime.now(timezone.utc).isoformat(),
    'state': 'USER_DEFERRED_EXACT_24_KNOWN_STRICT_STEP_FAILURES_NOT_PASSED',
    'userInstruction': '24项原有严格STEP失败 如果多次失败的可以先跳过吧、可以后面再做',
    'release': ref(release_path), 'priorBothConfigurations': ref(parent_path),
    'deferredCount': 24, 'exactTestNames': names,
    'activeDebugRun': 'artifacts/af1081-local86-1/full6830-debug-2',
    'activeRunChanged': False, 'strictGeometryAssertionsChanged': False,
    'tolerancesChanged': False, 'failedResultsConvertedToPassed': False,
    'otherSTEPFailuresDeferred': False,
    'policy': 'Proceed with remaining A-F work without waiting for these repairs. '
              'Report these exact cases separately as deferred known failures. '
              'Do not exclude whole parameterized methods or unrelated exchange cases.',
    'repairStillRequiredLater': True, 'AFComplete': False,
    'G': 'USER_DEFERRED', 'cleanup': False,
}
target.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'deferredKnownFailures': len(names), 'otherCasesDeferred': 0,
                  'testOutcomesChanged': False}))
