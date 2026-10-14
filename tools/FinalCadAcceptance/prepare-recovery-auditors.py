"""Project existing runtime assertions without changing their expressions."""
from pathlib import Path
import ast
import hashlib
import json
import sys

ROOT = Path(__file__).resolve().parents[2]
iteration = int(sys.argv[1])
assert 1 <= iteration <= 100
stage = ROOT / f'artifacts/af-final87-{iteration}'
out = stage / 'recovery-auditors'
assert not out.exists()
out.mkdir()
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref = lambda p: dict(path=p.relative_to(ROOT).as_posix(), sha256=sha(p))
rows = []
for name in ('audit-recovery24.py', 'audit-main-status36.py'):
    original = ROOT / 'tools/CurrentCadCandidate' / name
    text = original.read_text(encoding='utf-8-sig')
    projected = text.replace('PASS_LOCAL87CU1_CURRENT_F5_D7', 'PASS_FINAL87_CURRENT').replace('fullBoth7117=', 'fullBothNondeferred=')
    projected = projected.replace('currentF5D7/local87cu1', 'final87/current')
    before = [ast.dump(n) for n in ast.walk(ast.parse(text)) if isinstance(n, ast.Assert)]
    after = [ast.dump(n) for n in ast.walk(ast.parse(projected)) if isinstance(n, ast.Assert)]
    assert before == after
    path = out / name
    path.write_text(projected, encoding='utf-8')
    rows.append(dict(original=ref(original), projected=ref(path), originalAssertions=len(before), assertionsUnchanged=True))
# Only the metadata adapter is relocated. Resolve the repository explicitly.
adapter = ROOT / 'tools/FinalCadAcceptance/recovery-current.py'
text = adapter.read_text(encoding='utf-8-sig')
assert text.count('ROOT = Path(__file__).resolve().parents[2]') == 1
path = out / adapter.name
path.write_text(text.replace('ROOT = Path(__file__).resolve().parents[2]', f'ROOT = Path({str(ROOT)!r})'), encoding='utf-8')
record = dict(state='FINAL87_ORIGINAL_RECOVERY_ASSERTIONS_PROJECTED_NOT_RUNTIME', iteration=iteration,
              projections=rows, adapterSource=ref(adapter), adapter=ref(path), runtimeStarted=False, AFComplete=False)
(stage / 'recovery-auditors-preparation.json').write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
print('Both original recovery auditor assertion bodies preserved; final87 metadata adapter prepared, runtime NOT RUN.')
