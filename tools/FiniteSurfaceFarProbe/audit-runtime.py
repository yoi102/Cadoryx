"""Audit complete prerequisite runs; failed prerequisites stay failed."""
from datetime import datetime, timezone
from pathlib import Path
import hashlib
import json
import math
import sys

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'capability-evidence/diagnostics/af1081-local86'
DRAFT = ROOT / 'artifacts/af1081-local86-1'
REV = int(sys.argv[1]) if len(sys.argv) == 2 else 5
assert REV in [5, 6]

def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()

def ref(path):
    return {'path': path.relative_to(ROOT).as_posix(), 'sha256': sha(path)}

proof_path = OUT / ('far90-build-verification.json' if REV == 5 else f'far90-build-{REV}-verification.json')
proof = read(proof_path)
freeze = read(ROOT / proof['candidateFreeze']['path'])
assert sha(ROOT / proof['candidateFreeze']['path']) == proof['candidateFreeze']['sha256']
for row in proof['sourceInputs']:
    assert sha(ROOT / row['path']) == row['sha256']
cases = {r['name']: r for r in proof['cases']}
results = []
for build in proof['builds']:
    config = build['configuration']
    binary = Path(build['folder'])
    for row in build['payload']:
        assert sha(binary / row['path']) == row['sha256']
    folder = DRAFT / f'far-prerequisite/runtime-{config.lower()}-{REV}'
    raw = read(folder / 'result.json')
    terminal = read(folder / 'exit.json')
    rows = {r['name']: r for r in raw['rows']}
    assert raw['planned'] == raw['actual'] == len(raw['rows']) == len(rows) == 90
    assert set(rows) == set(cases) and raw['originalPlainCases'] == 30
    failed = [r for r in rows.values() if not r['passed']]
    assert raw['failures'] == len(failed) and raw['passed'] == (not failed)
    assert terminal['exitCode'] == (1 if failed else 0)
    assert len(raw['native']) == 62
    expected_native = {r['name']: r['sha256'] for r in freeze['native62']}
    assert {r['name'] for r in raw['native']} == set(expected_native)
    for row in raw['native']:
        path = Path(row['path'])
        assert path.parent == binary / 'occt'
        assert sha(path) == row['sha256'] == expected_native[row['name']]
    for name, case in cases.items():
        row = rows[name]
        case_folder = folder / name
        for prefix in ['source', 'stop']:
            assert sha(case_folder / f'{prefix}-before.brep') == sha(case_folder / f'{prefix}-after.brep')
        if not row['passed']:
            assert row['error']
            continue
        assert row['negative'] == case['negative']
        if case['negative']:
            assert row['buildStarted'] is False and row['effectiveArea'] <= 1e-8
            assert not (case_folder / 'result.brep').exists()
            continue
        assert row['buildStarted'] is True and (case_folder / 'result.brep').is_file()
        material = case['material']
        def measure(fn):
            return fn(2) - (0 if material == 'plain' else fn(1)) + (fn(.5) if material == 'island' else 0)
        sphere = case['kind'] == 'sphere'
        start = measure(lambda r: math.pi*r*r) if sphere else measure(lambda a: 4*a*a)
        integral = (measure(lambda r: 2*math.pi/3*(216 - (36-r*r)**1.5)) if sphere
                    else measure(lambda a: 2*a*(a*math.sqrt(36-a*a) + 36*math.asin(a/6))))
        cap = (measure(lambda r: 12*math.pi*(6-math.sqrt(36-r*r))) if sphere
               else measure(lambda a: 24*a*math.asin(a/6)))
        far = case['scenario'] in ['clip-full-far', 'raw-far-only']
        expected = 500 + start*13 + (integral if far else -integral)
        assert abs(row['expected'] - expected) < 1e-10
        assert abs(row['expectedStart'] - start) < 1e-10
        assert abs(row['expectedCap'] - cap) < 1e-10
        assert row['volumeTolerance'] == max(1e-6, 500*1e-9)
        assert row['startAreaTolerance'] == max(1e-7, start*1e-9)
        assert abs(row['capAreaTolerance'] - max(1e-7, cap*1e-9)) < 1e-18
        assert abs(row['volume'] - expected) <= row['volumeTolerance']
        assert abs(row['startArea'] - start) <= row['startAreaTolerance']
        assert abs(row['capArea'] - cap) <= row['capAreaTolerance']
        assert row['expectedDeltaSolids'] == (2 if material == 'island' else 1)
    results.append({'configuration': config, 'total': 90, 'passed': 90-len(failed),
                    'failed': len(failed), 'failures': failed, 'actualNative62Matched': True,
                    'originalSourceAndStopBrepBytesPreserved': True,
                    'result': ref(folder / 'result.json'), 'terminal': ref(folder / 'exit.json')})

target = OUT / ('far90-runtime-verification.json' if REV == 5 else f'far90-runtime-{REV}-verification.json')
assert not target.exists(), 'Preserve the original runtime audit.'
result = {'recordedAt': datetime.now(timezone.utc).isoformat(),
          'state': 'STRICT_FAR90_BOTH_ACTUALLY_EXECUTED',
          'build': ref(proof_path), 'configurations': results,
          'prerequisitePassed': all(r['failed'] == 0 for r in results),
          'productAlgorithmChanged': False, 'productAcceptanceProven': False,
          'AFComplete': False, 'G': 'USER_DEFERRED', 'cleanup': False}
target.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'configs': [{k: r[k] for k in ['configuration', 'passed', 'failed']} for r in results]}))
