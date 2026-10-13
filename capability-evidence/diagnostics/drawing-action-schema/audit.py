import hashlib
import json
import shutil
from pathlib import Path

root = Path('C:/Users/yoiri/source/repos/Cadoryx')
source = root / 'artifacts/drawing-action-schema-diagnostic-1'
target = root / 'capability-evidence/diagnostics/drawing-action-schema'
assert not target.exists(), 'Immutable evidence exists'
result = json.loads((source / 'result.json').read_text(encoding='utf-8-sig'))
freeze = json.loads((root / 'artifacts/af1076-root-full-1/freeze.json').read_text(encoding='utf-8-sig'))
digest = lambda path: hashlib.sha256(path.read_bytes()).hexdigest().upper()
for row in freeze['sourcePayload']:
    assert digest(root / row['path']) == row['sha256'], row['path']
assert len(result['rows']) == 12
summaries = []
for config in ('Release', 'Debug'):
    payload = next(c for c in freeze['configurations'] if c['configuration'] == config)
    expected = {p['path']: p['sha256'] for p in payload['payload']}
    for row in (r for r in result['rows'] if r['config'] == config):
        for p in row['payload']:
            assert expected[p['name']] == p['sha256']
        assert row['outputTokens'] == 1024
        assert row['promptTokens'] <= 8192 - 1024 - (1280 if row['aggressive'] else 640)
        assert not row['conditionalSurvivesWire']
        assert len(row['toolNames']) == (11 if row['variant'] == 'conditional' and row['aggressive'] else 12)
        if row['variant'] == 'conditional':
            original = next(r for r in result['rows'] if r['config'] == config and r['aggressive'] == row['aggressive'] and r['variant'] == 'original')
            assert row['wireCharacters'] == original['wireCharacters']
        summaries.append({k: row[k] for k in ('config', 'aggressive', 'variant', 'rawCharacters', 'wireCharacters', 'promptTokens', 'outputTokens') } | {'toolCount': len(row['toolNames'])})
calls = json.loads((root / 'artifacts/af1076-public85-1/lmstudio-drawing-1/tool-calls.json').read_text(encoding='utf-8-sig'))
errors = []
for ordinal, call in enumerate(calls, 1):
    if call['Name'] != 'cad_drawing':
        continue
    args = json.loads(call['ArgumentsJson'])
    reply = json.loads(call['result'])
    if args.get('action') == 'contract' and not reply.get('success') and 'documentId' in args:
        assert reply['allowedArguments'] == ['action', 'type']
        assert len(reply['validRequests']) == (1 if args.get('type') in ('sheet', 'view', 'dimension', 'datum', 'control_frame', 'balloon', 'hole_table', 'field', 'note', 'template', 'parts_table', 'zones') else 12)
        errors.append({'ordinal': ordinal, 'arguments': args, 'reply': reply})
assert errors, 'Observed production refusal missing'
assert any(len(e['reply']['validRequests']) == 12 for e in errors), 'Whole-catalog refusal missing'
assert result['nativeJobs'] == 0 and not result['productionChanged']
target.mkdir(parents=True)
files = []
for name in ('Program.cs', 'Probe.csproj', 'result.json'):
    shutil.copyfile(source / name, target / name)
    files.append({'path': name, 'sha256': digest(target / name)})
proof = {'state': 'PASS_DIAGNOSTIC_NOT_A_MODEL_WORKFLOW_FIX', 'frozenSourceFilesVerified': len(freeze['sourcePayload']),
         'source1076Changed': False, 'nativeJobs': 0, 'configurations': summaries, 'productionContractRefusals': errors,
         'files': files, 'conditionalProposal': 'REJECTED: removed on wire, retry loses one tool',
         'actionHintProposal': 'Short-history admission only; full original recovery budgets and model NOT RUN',
         'AFComplete': False, 'G': 'USER_DEFERRED', 'cleanup': False}
(target / 'verification.json').write_text(json.dumps(proof, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps({'state': proof['state'], 'rows': len(summaries), 'sourceFiles': proof['frozenSourceFilesVerified'], 'refusals': len(errors)}))
