"""Qualify auxiliary material builds without claiming runtime acceptance."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import re
import sys

ROOT = Path(__file__).resolve().parents[2]
iteration = int(sys.argv[1])
assert 1 <= iteration <= 100
stage = ROOT / f'artifacts/af-final87-{iteration}'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref = lambda p: dict(path=p.relative_to(ROOT).as_posix(), sha256=sha(p))
freeze, prep, built = (read(stage / n) for n in ('freeze.json', 'material-preparation.json', 'material-build-exit.json'))
assert prep['freeze']['sha256'] == sha(stage / 'freeze.json') and len(built['stages']) == 2
for row in prep['sourcePayload']:
    assert sha(ROOT / row['path']) == row['sha256']
for row in prep['observer']:
    assert row['byteIdentical'] and sha(ROOT / row['original']['path']) == sha(ROOT / row['current']['path']) == row['original']['sha256']
old = (ROOT / prep['priorProgram']['path']).read_text(encoding='utf-8-sig')
original = (ROOT / prep['originalProgram']['path']).read_text(encoding='utf-8-sig')
assert old.replace('8.0.1-preview.31.cadoryx.generated-defeature-history.30', '8.0.1-preview.31.cadoryx.curve-inspection.26').replace('Expected SDK30.', 'Expected SDK26.').replace('unified SDK30 viewport', 'revision9 SDK26 viewport') == original
projected = (ROOT / prep['projectedProgram']['path']).read_text(encoding='utf-8-sig')
assert projected.replace(freeze['version'], '8.0.1-preview.31.cadoryx.generated-defeature-history.30').replace('Expected final87.', 'Expected SDK30.').replace('final87 viewport', 'unified SDK30 viewport') == old
native = {r['name']: r['sha256'] for r in freeze['native62']}
managed = {r['name']: r['sha256'] for r in prep['expectedManaged30']}
assert len(managed) == 30 and len(native) == 62
hosts = []
for row in built['stages']:
    assert row['exitCode'] == 0
    folder, log = Path(row['output']), Path(row['log'])
    assert not re.search(r'\b(?:warning|error)\s+(?:[A-Z]+\d+|:)', log.read_text(encoding='utf-8-sig'), re.I)
    if row['label'] == 'material82':
        assert all(sha(folder / n) == h for n, h in managed.items())
        assert {p.name: sha(p) for p in (folder / 'occt').glob('*.dll')} == native
        assert sha(folder / 'expected.json') == sha(stage / 'material82/expected.json')
    else:
        assert (folder / 'MaterialDesktopObserver.dll').is_file()
        assert not list(folder.glob('OcctSharp*.dll')) and not (folder / 'occt').exists()
    hosts.append(dict(row, payload=[dict(path=p.relative_to(folder).as_posix(), sha256=sha(p)) for p in sorted(folder.rglob('*')) if p.is_file()]))
record = dict(recordedAt=datetime.now(timezone.utc).isoformat(), state='FINAL87_ORIGINAL_MATERIAL82_AND_OBSERVER_BUILD0_QUALIFIED_NOT_RUNTIME',
              freeze=ref(stage / 'freeze.json'), preparation=ref(stage / 'material-preparation.json'), builds=hosts,
              original82CasesAnd346ImagesRequired=True, observerCSharpByteIdentical=True,
              expectedManaged30=prep['expectedManaged30'], native62=freeze['native62'],
              realPickerAndProductionActionsRequired=True, runtimeStarted=False, desktopInputStarted=False, AFComplete=False)
out = stage / 'material-build-verification.json'
if out.exists():
    before = read(out)
    assert {k: v for k, v in before.items() if k != 'recordedAt'} == {k: v for k, v in record.items() if k != 'recordedAt'}
else:
    out.write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
print('Original material82 and exact observer build/payload qualified; no runtime or desktop acceptance claimed.')
