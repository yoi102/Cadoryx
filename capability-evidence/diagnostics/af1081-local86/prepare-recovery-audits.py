"""Keep the original runtime assertions while updating only current identities."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json

ROOT = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()
records = []
for label, name, start, end, terminal_old in [
    ('recovery24', 'verify-af1063r5-wpf-recovery24-1.py', 'terminal_path =', 'output = draft /',
     'PROCESS_PASS_OWNED_WPF_RECOVERY24_PENDING_AUDIT_IMAGES_NOT_PHYSICAL_CONTEXT_FAULT'),
    ('main-status36', 'verify-af1063r5-main-status-recovery-1.py', 'terminal_path =', 'record = dict(',
     'PROCESS_PASS_ACTUAL_MAIN_STATUS_RECOVERY36_PENDING_INDEPENDENT_AUDIT')]:
    original = ROOT / 'artifacts' / name
    text = original.read_text(encoding='utf-8-sig')
    body = text[text.index(start):text.index(end)]
    candidate_body = body.replace(terminal_old, 'PROCESS_PASS_PENDING_INDEPENDENT_AUDIT_AND_IMAGE_REVIEW')
    assert candidate_body.replace('PROCESS_PASS_PENDING_INDEPENDENT_AUDIT_AND_IMAGE_REVIEW', terminal_old) == body
    header = '''import datetime
import itertools
import json
import struct
from pathlib import Path
import importlib.util
spec = importlib.util.spec_from_file_location('recovery_current', Path(__file__).with_name('recovery-current.py'))
current = importlib.util.module_from_spec(spec)
spec.loader.exec_module(current)
read, sha, ref, a = current.read, current.sha, current.ref, current.a
'''
    header += f"app, full_proofs, fixture, fixture_path, prep_path = current.accepted_app('{label}')\ndraft = current.DRAFT / '{label}'\n"
    if label == 'recovery24':
        footer = '''output = draft / 'runtime-1-verification.json'
assert not output.exists()
record = dict(recordedAt=datetime.datetime.now(datetime.timezone.utc).isoformat(),
    state='PASS_LOCAL86_SOURCE1081_ORIGINAL_WPF24_NATIVE62_HISTORY_SAVE48_IMAGES52_PENDING_DIRECT_REVIEW',
    preparation=ref(prep_path), fixture=ref(fixture_path), semanticBuild=app['build'],
    fullBoth6830=full_proofs, terminal=ref(terminal_path), result=ref(result_path), binding=ref(binding_path),
    rawStderrBytes=0, cases=cases, images=images, saves=saves, uniqueWholeImages=list(unique.values()),
    originalProgramByteIdentical=True, original24CaseAssertionsAnd30sLimitsRetained=True,
    newWholeImageReview='PENDING_DIRECT_REVIEW_EVERY_UNIQUE_FULL_FRAME', actualAbsentOrInvalidContext='NOT_RUN',
    physicalGpuRemoval='NOT_RUN', mainShellStatusBar='SEPARATE_MAIN36_REQUIRED', productionApplied=False,
    publiclyPublished=False, desktopInputStarted=False, AFComplete=False, G='USER_DEFERRED')
output.write_text(json.dumps(record, indent=2)+'\\n', encoding='utf-8')
print('PASS current1081/local86 original24/native62/48 saves/52 whole images; direct image review remains separate.')
'''
    else:
        footer = '''out = draft / 'runtime-1-verification.json'
assert not out.exists()
record = dict(recordedAt=datetime.datetime.now(datetime.timezone.utc).isoformat(),
    state='PASS_LOCAL86_SOURCE1081_MAIN_STATUS_RECOVERY36_NATIVE62_144_FULL_IMAGES_PENDING_DIRECT_REVIEW',
    fixture=ref(fixture_path), application=app['build'], fullBoth6830=full_proofs,
    terminal=ref(terminal_path), result=ref(result_path), binding=ref(binding_path),
    cases=sorted(expected), passed=36, failed=0, frames=frames, mainStatusBindingVerified=True,
    actualProductionNativeLossAndOtherPaneVerified=True, observationsBeforeFixtureFallback=True,
    original30sReadinessAnd900sObservationRetained=True, postFaultNativeEditUndoRedoSaveReopen=True,
    originalWpf24='SEPARATE_CURRENT_BUILD_REPLAY_REQUIRED', wholeImageReview='PENDING_SEPARATE_DIRECT_REVIEW',
    shortRecoveryBeginningMessagePhysicalScanout='NOT_CLAIMED', actualAbsentOrInvalidContext='NOT_RUN',
    physicalGpuRemoval=False, desktopInputStarted=False, productionApplied=False, publiclyPublished=False,
    AFComplete=False, G='USER_DEFERRED')
out.write_text(json.dumps(record, indent=2)+'\\n', encoding='utf-8')
print('PASS current1081/local86 main status36/native62/model-history/144 whole images; direct review remains separate.')
'''
    target = OUT / f'audit-{label}.py'
    assert not target.exists()
    result = header + candidate_body + footer
    compile(result, str(target), 'exec')
    target.write_text(result, encoding='utf-8')
    records.append({'label': label, 'original': {'path': str(original), 'sha256': sha(original)},
                    'candidate': {'path': str(target), 'sha256': sha(target)},
                    'originalRuntimeValidationBodySha256': hashlib.sha256(body.encode()).hexdigest().upper(),
                    'runtimeAssertionsRetainedExceptTerminalLabel': True})
proof = OUT / 'recovery-auditors-preparation.json'
assert not proof.exists()
proof.write_text(json.dumps({'recordedAt': datetime.now(timezone.utc).isoformat(), 'auditors': records,
                             'runtime': 'NOT RUN', 'nativeJobs': 0, 'desktopInput': False}, indent=2)+'\n', encoding='utf-8')
print(json.dumps({'originalRuntimeValidationBodiesRetained': True, 'compiledPythonSyntax': True, 'runtime': 'NOT RUN'}))
