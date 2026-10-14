"""Persist small qualification and unresolved-window evidence without binaries."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib, json, shutil

ROOT = Path(__file__).resolve().parents[2]
STAGE = ROOT / 'artifacts/f5-lmstudio-completion-integrity-1'
OUT = ROOT / 'capability-evidence/diagnostics/f5-lmstudio-completion-integrity'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref = lambda p: dict(path=p.relative_to(ROOT).as_posix(), sha256=sha(p))
qualification_path = STAGE / 'qualification4.json'; qualified = read(qualification_path)
for row in qualified['qualifiedSources']: assert sha(ROOT / row['path']) == row['sha256']
for row in qualified['appPayload']: assert sha(Path(qualified['application']) / row['path']) == row['sha256']
assert [r['passed'] for r in qualified['regression']] == [8, 40, 40, 97, 97]
OUT.mkdir(parents=True, exist_ok=True)
paths = [STAGE / f'qualification{i}.json' for i in (2, 3, 4)]
paths += [STAGE / r['trx']['path'].split('f5-lmstudio-completion-integrity-1/')[1] for r in qualified['regression']]
paths += [ROOT / r['path'] for r in qualified['builds'] + qualified['preservedRawBuildFailures']]
paths += [STAGE / p for p in ('provider-drawing-1/wire-response-019.json',
    'provider-drawing-1/response-019-failure.json', 'provider-drawing-2/events.json',
    'provider-drawing-2/wire-response-019.json', 'provider-drawing-2/wire-response-022.json',
    'provider-drawing-2/response-022-failure.json', 'raw-source1/projection-verification.json',
    'd6-operator2.json')]
window = ROOT / 'artifacts/af1082-f5-d7-local87cu1-1/windows/window-create-en-US-plane-positive-2'
window_state, window_failure = read(window / 'state.json'), read(window / 'failure.json')
assert window_failure['state'] == 'FAIL_OWNED_D6_WINDOW_OBSERVER'
assert 'Required actual control trace incomplete' in window_failure['error']
assert not window_state['evidence']
assert all(r['originalSnapshotUnchanged'] and not r['hasPreview'] for r in window_state['transitions'])
assert window_state['transitions'][-1]['start']['StartY'] == -1
assert not read(STAGE / 'd6-operator2.json')['confirmationInputIssued']
paths += [window / 'state.json', window / 'failure.json', Path(str(window) + '-exit.json')]
copied = []
for source in paths:
    if source.is_relative_to(STAGE): relative = source.relative_to(STAGE)
    else: relative = Path('d6-second-attempt') / source.name
    target = OUT / 'records' / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    if target.exists(): assert sha(target) == sha(source)
    else: shutil.copyfile(source, target)
    assert sha(target) == sha(source)
    copied.append(dict(source=ref(source), persisted=ref(target)))
out = OUT / 'checkpoint.json'
assert not out.exists()
record = dict(recordedAt=datetime.now(timezone.utc).isoformat(),
    state='F5_BOUNDED_RECOVERY_40_BOTH_RELATED97_BOTH_D6_UNRESOLVED_CHECKPOINT',
    qualification=ref(qualification_path), sources=qualified['qualifiedSources'], persisted=copied,
    full7160Rerun=False, originalSTEP24Retried=False,
    STEP24='EXACT_ORIGINAL_FAILURES_USER_DEFERRED',
    D6SecondActualAttempt='FAIL_CANCELLED_NO_CONFIRM_NO_PREVIEW_ORIGINAL_SNAPSHOT_UNCHANGED',
    D6Accepted=False, workspaceCurrentRecovery='NOT_RUN',
    SDK='EXACT_LOCAL_8.0.1-preview.87.cu1_UNCHANGED', AFComplete=False, G='USER_DEFERRED', cleanup=False)
out.write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
print('Persisted qualified recovery/TRX/builds/raw failures and unresolved D6 second attempt; no binaries/caches.')
