"""Freeze the built 90-case prerequisite against the unchanged SDK candidate."""
from datetime import datetime, timezone
from pathlib import Path
import hashlib
import itertools
import json
import sys

ROOT = Path(__file__).resolve().parents[2]
DRAFT = ROOT / 'artifacts/af1081-local86-1'
OUT = ROOT / 'capability-evidence/diagnostics/af1081-local86'
REV = int(sys.argv[1]) if len(sys.argv) == 2 else 5
assert REV in [5, 6]

def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()

def ref(path):
    return {'path': path.relative_to(ROOT).as_posix(), 'sha256': sha(path)}

freeze = read(DRAFT / 'freeze.json')
builds = []
for config in ['Release', 'Debug']:
    receipt_path = DRAFT / f'far-prerequisite-build-exit-{config.lower()}-{REV}.json'
    receipt = read(receipt_path)
    assert receipt['exitCode'] == 0 and receipt['configuration'] == config
    assert receipt['plannedCases'] == 90 and receipt['strictProductionBudgets']
    for row in receipt['sourceInputs']:
        assert sha(ROOT / row['path']) == row['sha256']
    folder = Path(receipt['output'])
    candidate = Path(receipt['candidate'])
    for row in freeze['managed13']:
        assert sha(candidate / row['name']) == sha(folder / row['name']) == row['sha256']
    assert {p.name for p in (folder / 'occt').glob('*.dll')} == {r['name'] for r in freeze['native62']}
    for row in freeze['native62']:
        assert sha(candidate / 'occt' / row['name']) == sha(folder / 'occt' / row['name']) == row['sha256']
    for name in ['Cadoryx.Db.dll', 'Cadoryx.Kernel.Abstractions.dll', 'Cadoryx.Kernel.Occt.dll']:
        assert sha(candidate / name) == sha(folder / name)
    builds.append({'configuration': config, 'receipt': ref(receipt_path),
                   'folder': str(folder), 'candidate': str(candidate),
                   'payload': [{'path': p.relative_to(folder).as_posix(), 'sha256': sha(p)}
                               for p in sorted(folder.rglob('*')) if p.is_file()],
                   'sdkManaged13AndNative62Matched': True,
                   'productionKernelAndContractsMatched': True})

cases = []
for kind, pose, material, scenario in itertools.product(
        ['sphere', 'cylinder'], ['identity', 'negative', 'tilted'],
        ['plain', 'hole', 'island'],
        ['raw-full-near', 'clip-full-near', 'clip-full-far', 'raw-far-only', 'clip-near-only-far']):
    name = f'{kind}-{pose}-{scenario}' + ('' if material == 'plain' else f'-{material}')
    cases.append({'name': name, 'kind': kind, 'pose': pose, 'material': material,
                  'scenario': scenario, 'negative': scenario == 'clip-near-only-far'})
assert len(cases) == len({r['name'] for r in cases}) == 90
assert sum(r['material'] == 'plain' for r in cases) == 30
assert sum(r['negative'] for r in cases) == 18

target = OUT / ('far90-build-verification.json' if REV == 5 else f'far90-build-{REV}-verification.json')
assert not target.exists(), 'Do not overwrite a recorded build.'
result = {'recordedAt': datetime.now(timezone.utc).isoformat(),
          'state': 'STRICT_FAR90_BOTH_BUILDS_FROZEN_NOT_RUNTIME_OR_PRODUCT_ACCEPTANCE',
          'sourceInputs': builds and read(DRAFT / f'far-prerequisite-build-exit-release-{REV}.json')['sourceInputs'],
          'candidateFreeze': ref(DRAFT / 'freeze.json'), 'builds': builds, 'cases': cases,
          'positiveCases': 72, 'negativeCases': 18, 'oldPlainCaseNamesRetained': True,
          'volumeTolerance': 'max(1e-6, sourceVolume*1e-9)',
          'areaTolerance': 'max(1e-7, expectedArea*1e-9)',
          'runtimeExecuted': False, 'productAlgorithmChanged': False,
          'AFComplete': False, 'G': 'USER_DEFERRED', 'cleanup': False}
target.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'bothBuilds': len(builds), 'cases': len(cases), 'runtimeExecuted': False}))
