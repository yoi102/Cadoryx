import datetime
import itertools
import json
import struct
import sys
from pathlib import Path
import importlib.util
spec = importlib.util.spec_from_file_location('recovery_current', Path(__file__).with_name('recovery-current.py'))
current = importlib.util.module_from_spec(spec)
spec.loader.exec_module(current)
read, sha, ref, a = current.read, current.sha, current.ref, current.a
app, full_proofs, fixture, fixture_path, prep_path = current.accepted_app('recovery24')
iteration = int(sys.argv[1]) if len(sys.argv) == 2 else 1
assert 1 <= iteration <= 100
runtime = f'runtime-{iteration}'
draft = current.DRAFT / 'recovery24'
terminal_path = draft / (runtime + '-exit.json')
terminal = read(terminal_path)
assert terminal['state'] == 'PROCESS_PASS_PENDING_INDEPENDENT_AUDIT_AND_IMAGE_REVIEW'
assert terminal['exitCode'] == 0 and not terminal['productionApplied'] and not terminal['desktopInputStarted']
assert terminal['observationDeadlineSeconds'] == 900
proof_path = Path(fixture['compiledAgainst']['path']);proof = read(proof_path)
candidate = Path(fixture['candidate'])
for row in fixture['payload']:assert sha(candidate/row['path']) == row['sha256']
assert (draft/(runtime + '-stderr.log')).stat().st_size == 0
result_path = Path(terminal['output']) / 'result.json'
result = read(result_path)
assert result['state'] == 'PASS_OWNED_WPF_AUTO_RECOVERY24_PENDING_INDEPENDENT_AUDIT'
assert not result['desktopInputStarted'] and not result['physicalGpuRemoval']
assert result['actualAbsentOrInvalidContext'] == result['mainShellStatusBar'] == 'NOT_RUN'
expected_native = {row['name']: row['sha256'] for row in proof['native62']}
binding_path = draft / (runtime + '-loaded-native.json')
for observed in (result['native'], read(binding_path)['native']):
    assert len(observed) == 62 and {row['name']: row['sha256'] for row in observed} == expected_native
    for row in observed:
        assert sha(Path(row['path'])) == row['sha256']
        assert Path(row['path']).resolve().parent == (candidate / 'occt').resolve()
losses = ('native-presentation-loss', 'native-owner-disposed')
phases = ('completed', 'progressive')
routes = ('recover', 'close-before-dispatch', 'cleanup-callback-fails')
expected = set(itertools.product(losses, phases, (1, 2), routes))
cases = result['cases']
assert len(cases) == 24 and {(r['loss'], r['phase'], r['paneCount'], r['route']) for r in cases} == expected
assert len({row['name'] for row in cases}) == 24
images, saves = [], []
for row in cases:
    assert row['passed'] and row['failure'] is None and row['emergencyCleanupExcludedFromOutcome']
    assert row['finalHosts'] == 0
    assert row['finalAssets'] == row['reopenedAssets'] == {'Count': 0, 'SizeBytes': 0}
    observation = row['observation']
    assert observation is not None and not observation['oldHandleStillWindow']
    assert observation['oldVisible'] == observation['oldGeometry'] == observation['oldSource'] == observation['oldXde'] == 0
    assert observation['health']['ContextPreviouslyValid'] and observation['health']['RequiresNewViewer']
    assert observation['health']['OwnerDisposed'] == (row['loss'] == 'native-owner-disposed')
    if row['loss'] == 'native-owner-disposed':
        assert observation['health']['NativeHealth'] == 0
    else:
        assert observation['health']['NativeHealth'] != 0
    closing = row['route'] == 'close-before-dispatch'
    recovering = row['route'] == 'recover'
    assert observation['attempts'] == (0 if closing else 1)
    assert observation['completed'] == (1 if recovering else 0)
    assert observation['hosts'] == (0 if closing else row['paneCount'] if recovering else row['paneCount'] - 1)
    checks = {'exact_document_history_generation_file_and_saved_state_preserved_without_command_replay',
              'same_session_native_authoring_exact_undo_redo_and_production_save_reopen'}
    if recovering:
        checks.add('coalesced_exact_host_replaced_current_snapshot_camera_and_complete_frame')
    if row['paneCount'] == 2 and not closing:
        checks.add('other_real_pane_owner_and_complete_frame_unchanged')
    assert len(row['checks']) == len(checks) and set(row['checks']) == checks
    kinds = {'before'}
    if row['paneCount'] == 2:
        kinds.add('other-before')
        if not closing:
            kinds.add('other-after')
    if recovering:
        kinds.add('after')
    assert len(row['images']) == len(kinds) and {r['kind'] for r in row['images']} == kinds
    by_kind = {}
    for image in row['images']:
        path = Path(image['path'])
        assert path.resolve().parent == (Path(terminal['output']) / row['name']).resolve()
        assert ref(path) == {'path': image['path'], 'sha256': image['sha256']}
        data = path.read_bytes()
        assert data[:8] == b'\x89PNG\r\n\x1a\n'
        width, height = struct.unpack('>II', data[16:24])
        assert width > 0 and height > 0
        by_kind[image['kind']] = data
        images.append(dict(case=row['name'], **image, width=width, height=height))
    if recovering:
        assert by_kind['before'] == by_kind['after']
    if 'other-after' in by_kind:
        assert by_kind['other-before'] == by_kind['other-after']
    for stage in ('before', 'after'):
        path = Path(terminal['output']) / row['name'] / (stage + '.cadoryx')
        assert path.stat().st_size > 0
        saves.append(dict(case=row['name'], stage=stage, **ref(path)))
assert len(images) == 52 and len(saves) == 48
assert {str(p) for p in Path(terminal['output']).rglob('*.png')} == {row['path'] for row in images}
unique = {}
for row in images:
    unique.setdefault(row['sha256'], row)
output = draft / (runtime + '-verification.json')
assert not output.exists()
record = dict(recordedAt=datetime.datetime.now(datetime.timezone.utc).isoformat(),
    state='PASS_LOCAL87CU1_CURRENT_F5_D7_ORIGINAL_WPF24_NATIVE62_HISTORY_SAVE48_IMAGES52_PENDING_DIRECT_REVIEW',
    preparation=ref(prep_path), fixture=ref(fixture_path), semanticBuild=app['build'],
    fullBoth7117=full_proofs, terminal=ref(terminal_path), result=ref(result_path), binding=ref(binding_path),
    rawStderrBytes=0, cases=cases, images=images, saves=saves, uniqueWholeImages=list(unique.values()),
    originalProgramByteIdentical=True, original24CaseAssertionsAnd30sLimitsRetained=True,
    newWholeImageReview='PENDING_DIRECT_REVIEW_EVERY_UNIQUE_FULL_FRAME', actualAbsentOrInvalidContext='NOT_RUN',
    physicalGpuRemoval='NOT_RUN', mainShellStatusBar='SEPARATE_MAIN36_REQUIRED', productionApplied=False,
    publiclyPublished=False, desktopInputStarted=False, AFComplete=False, G='USER_DEFERRED')
output.write_text(json.dumps(record, indent=2)+'\n', encoding='utf-8')
print('PASS currentF5D7/local87cu1 original24/native62/48 saves/52 whole images; direct image review remains separate.')
