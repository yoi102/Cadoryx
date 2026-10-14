"""Audit the original82 names, payload, lifecycle and all346 full frames."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import struct
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
iteration = int(sys.argv[1])
assert 1 <= iteration <= 100
stage = ROOT / f'artifacts/af-final87-{iteration}'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref = lambda p: dict(path=p.relative_to(ROOT).as_posix(), sha256=sha(p))
for name in ('audit-full.py', 'audit-material-build.py'):
    subprocess.run([sys.executable, str(Path(__file__).with_name(name)), str(iteration)], check=True)
prep, run = (read(stage / n) for n in ('material-preparation.json', 'material82-runtime1-exit.json'))
assert run['state'] == 'PROCESS_PASS_ORIGINAL82_PENDING_INDEPENDENT_PAYLOAD_AND_IMAGE_AUDIT' and run['exitCode'] == 0
assert not run['desktopInputStarted'] and run['planned'] == 82
publish, output = Path(run['publish']), Path(run['output'])
result_path = output / 'result.json'
result = read(result_path)
assert result['state'] == 'PASS' and result['passed'] == result['planned'] == 82 and result['failed'] == 0
old = read(Path(prep['original82']['path']))
old_result = read(Path(old['result']['path']))
assert len(result['results']) == 82 and {r['name'] for r in result['results']} == {r['name'] for r in old_result['results']}
assert all(r['status'] == 'PASS' and r['error'] is None and r['assets']['Count'] == r['assets']['SizeBytes'] == 0 for r in result['results'])
assert len(result['managed']) == 30 and len(result['native']) == 62
assert {r['name']: r['sha256'] for r in result['managed']} == {r['name']: r['sha256'] for r in prep['expectedManaged30']}
native = {r['name']: r['sha256'] for r in prep['native62']}
assert {r['name']: r['sha256'] for r in result['native']} == native
for row in result['managed'] + result['native']:
    path = Path(row['path'])
    assert sha(path) == row['sha256']
    assert path.parent == publish / ('occt' if row in result['native'] else '')
binding_path = Path(run['binding'])
binding = read(binding_path)
assert binding['processId'] == run['pid'] and len(binding['native']) == 62
assert {r['name']: r['sha256'] for r in binding['native']} == native
for row in binding['native']:
    assert Path(row['path']).parent == publish / 'occt' and sha(Path(row['path'])) == row['sha256']
expected_path = publish / 'expected.json'
assert result['expectedSha256'] == sha(expected_path) == sha(stage / 'material82/expected.json')
assert read(expected_path)['build']['sha256'] == sha(stage / 'freeze.json')
assert (stage / 'material82-runtime1-stderr.log').stat().st_size == 0
assert len(result['frames']) == 346
old_frames = {Path(r['path']).name: r for r in old_result['frames']}
frames = []
for row in result['frames']:
    path, prior = Path(row['path']), old_frames[Path(row['path']).name]
    data = path.read_bytes()
    assert sha(path) == row['sha256']
    assert data[:8] == b'\x89PNG\r\n\x1a\n' and struct.unpack('>II', data[16:24]) == (row['width'], row['height']) == (480, 320)
    assert sha(Path(prior['path'])) == prior['sha256']
    frames.append(dict(row, acceptedPrior=ref(Path(prior['path'])), fullBytesEqual=data == Path(prior['path']).read_bytes()))
record = dict(recordedAt=datetime.now(timezone.utc).isoformat(), state='FINAL87_ORIGINAL82_NATIVE62_346_FULL_IMAGES_PENDING_DIRECT_REVIEW',
              preparation=ref(stage / 'material-preparation.json'), freeze=ref(stage / 'freeze.json'), terminal=ref(stage / 'material82-runtime1-exit.json'),
              result=ref(result_path), binding=ref(binding_path), passed=82, failed=0, frames=frames,
              original82NamesAndAssertionsRetainedExceptRuntimeIdentity=True, zeroAssetsEach=True,
              wholeImageReview='SEPARATE_REQUIRED', materialDesktopUi=False, physicalGpuLoss=False, performanceMeasured=False, AFComplete=False)
out = ROOT / f'capability-evidence/diagnostics/af-final87/candidate{iteration}-material82-verification.json'
if out.exists():
    before = read(out)
    assert {k: v for k, v in before.items() if k != 'recordedAt'} == {k: v for k, v in record.items() if k != 'recordedAt'}
else:
    out.write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
print('Original82 exact names/native62/lifecycle and346 full frames verified; direct image review and material UI remain separate.')
