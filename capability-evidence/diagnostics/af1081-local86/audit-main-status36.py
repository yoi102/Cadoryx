import datetime
import itertools
import json
import struct
from pathlib import Path
import importlib.util
spec = importlib.util.spec_from_file_location('recovery_current', Path(__file__).with_name('recovery-current.py'))
current = importlib.util.module_from_spec(spec)
spec.loader.exec_module(current)
read, sha, ref, a = current.read, current.sha, current.ref, current.a
app, full_proofs, fixture, fixture_path, prep_path = current.accepted_app('main-status36')
draft = current.DRAFT / 'main-status36'
terminal_path = draft / 'runtime-1-exit.json'
terminal = read(terminal_path)
assert terminal['state'] == 'PROCESS_PASS_PENDING_INDEPENDENT_AUDIT_AND_IMAGE_REVIEW'
assert terminal['exitCode'] == 0 and terminal['planned'] == 36
assert terminal['readinessSecondsEach'] == 30 and terminal['observationDeadlineSeconds'] == 900
result_path = Path(terminal['output']) / 'result.json'
result = read(result_path)
assert result['state'] == 'PASS_ACTUAL_MAIN_STATUS_RECOVERY36_PENDING_INDEPENDENT_AUDIT'
assert result['planned'] == result['actual'] == len(result['cases']) == 36
assert result['failure'] is None and result['observationsBeforeFixtureFallback']
assert result['finalHosts'] == result['finalAssets']['Count'] == result['finalAssets']['SizeBytes'] == 0
matrix = fixture['matrix']
expected = {'-'.join(map(str, case)) for case in itertools.product(matrix['cultures'], matrix['nativeLosses'], matrix['routes'], matrix['failedActivePane'])}
assert len(expected) == 36 and {row['name'] for row in result['cases']} == expected
assert len(result['transitions']) == 36 and {row['name'] for row in result['transitions']} == expected
frames = []
for case in result['cases']:
    assert case['passed'] and case['failure'] is None
    assert len(case['checks']) == 4 and len(set(case['checks'])) == 4
    assert case['finalStatus'] == case['boundStatusText']
    expected_attempts = 0 if case['route'] == 'detach-before-dispatch' else 1
    assert case['attempts'] == expected_attempts
    assert case['completed'] == (1 if case['route'] == 'recover' else 0)
    images = {row['kind']: row for row in case['images']}
    required = {'before', 'other-before', 'complete-wpf-shell-final-status'}
    if case['route'] != 'detach-before-dispatch':
        required.add('other-after')
        assert Path(images['other-before']['path']).read_bytes() == Path(images['other-after']['path']).read_bytes()
    if case['route'] == 'recover':
        required.add('after')
        assert Path(images['before']['path']).read_bytes() == Path(images['after']['path']).read_bytes()
    assert set(images) == required and len(case['images']) == len(required)
    for row in case['images']:
        path = Path(row['path'])
        data = path.read_bytes()
        assert sha(path) == row['sha256'] and data[:8] == b'\x89PNG\r\n\x1a\n'
        width, height = struct.unpack('>II', data[16:24])
        assert width > 0 and height > 0
        frames.append(dict(case=case['name'], kind=row['kind'], image=ref(path), width=width, height=height))
assert len(frames) == 144
binding_path = Path(terminal['native'])
binding = read(binding_path)
assert len(binding['native']) == 62
assert {row['name']: row['sha256'] for row in binding['native']} == {row['name']: row['sha256'] for row in app['native']}
for row in binding['native']:
    assert ref(Path(row['path']))['sha256'] == row['sha256']
    assert Path(row['path']).resolve().parent == (Path(app['candidate']) / 'occt').resolve()
assert (draft / 'runtime-1-stderr.log').stat().st_size == 0
assert not result['desktopInputStarted'] and not result['physicalGpuRemoval']
assert result['actualAbsentOrInvalidContext'] == 'NOT_RUN'
out = draft / 'runtime-1-verification.json'
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
out.write_text(json.dumps(record, indent=2)+'\n', encoding='utf-8')
print('PASS current1081/local86 main status36/native62/model-history/144 whole images; direct review remains separate.')
