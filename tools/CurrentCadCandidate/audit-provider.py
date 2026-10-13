"""Audit original provider outcomes against the current frozen application."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import runpy

ROOT = Path(__file__).resolve().parents[2]
DRAFT = ROOT / 'artifacts/af1082-f5-d7-local87cu1-1'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
def ref(p): return {'path': p.relative_to(ROOT).as_posix(), 'sha256': sha(p)}

runpy.run_path(str(Path(__file__).with_name('require-full-gate.py')))
freeze_path = DRAFT / 'provider-runtime-freeze.json'
suite_path = DRAFT / 'provider-both-exit.json'
freeze, suite = read(freeze_path), read(suite_path)
parent = read(DRAFT / 'freeze.json')
candidate = Path(freeze['candidate'])
assert candidate == Path(parent['provider'])
assert freeze['model']['identifier'] == 'prism-ml/bonsai-27b'
assert freeze['model']['selectedVariant'] == 'prism-ml/bonsai-27b@q1_0'
assert freeze['model']['contextLength'] == freeze['originalContext'] == 8192
assert freeze['originalToolRounds'] == 16 and freeze['originalPerTurnMinutes'] == 4
assert freeze['endpoint'] == 'http://127.0.0.1:15630/v1'
assert freeze['scenarioOrder'] == ['drawing', 'workspace']
for row in freeze['payload']: assert sha(candidate / row['path']) == row['sha256']
for row in parent['providerPayload']: assert sha(candidate / row['path']) == row['sha256']
assert len(freeze['originalPromptAndAssertionSources']) == 3
for row in freeze['originalPromptAndAssertionSources']:
    assert sha(ROOT / row['path']) == row['sha256']
expected = {r['name']: r['sha256'] for r in parent['native62']}
assert expected == {r['name']: r['sha256'] for r in freeze['native62']} and len(expected) == 62
assert len(suite['scenarios']) == 2
assert [r['scenario'] for r in suite['scenarios']] == freeze['scenarioOrder']
cases = []
for gate in suite['scenarios']:
    name = gate['scenario']
    terminal_path = Path(gate['terminal'])
    assert sha(terminal_path) == gate['sha256']
    terminal = read(terminal_path)
    folder = DRAFT / f'lmstudio-{name}-1'
    result_path, calls_path = folder / 'result.json', folder / 'tool-calls.json'
    result, calls = read(result_path), read(calls_path)
    assert result['scenario'] == name and result['provider'] == terminal['provider'] == 'lmstudio'
    assert result['model'] == terminal['model'] == 'prism-ml/bonsai-27b'
    assert result['endpoint'] == terminal['endpoint'] == freeze['endpoint']
    assert result['actualProductionTools'] and result['assetsAfterClose'] == 0
    assert terminal['exitCode'] == gate['exitCode'] == (0 if result['passed'] else 1)
    assert not terminal['desktopInputStarted'] and not terminal['productionApplied']
    assert 0 <= len(result['turns']) <= 8
    assert [r['turn'] for r in result['turns']] == list(range(1, len(result['turns']) + 1))
    if result['passed']: assert len(result['turns']) == 8 and result['failure'] is None
    else: assert result['failure']
    binding_path = Path(terminal['binding'])
    binding = read(binding_path)
    assert len(binding['native']) == 62
    assert {r['name']: r['sha256'] for r in binding['native']} == expected
    for row in binding['native']:
        assert Path(row['path']).parent == candidate / 'occt'
        assert sha(Path(row['path'])) == row['sha256']
    rejected = []
    for ordinal, call in enumerate(calls, 1):
        receipt = json.loads(call['result'])
        if receipt.get('success') is False:
            rejected.append({'ordinal': ordinal, 'tool': call['Name'],
                             'arguments': json.loads(call['ArgumentsJson']), 'receipt': receipt})
    requests = sorted(folder.glob('request-*.json'))
    captures = []
    for request in requests:
        wire = folder / request.name.replace('request-', 'wire-request-')
        response = folder / request.name.replace('request-', 'response-')
        # Interrupted HTTP exchanges keep their partial evidence and fail the audit.
        assert wire.exists() and response.exists()
        captures.append({'request': ref(request), 'wire': ref(wire), 'response': ref(response)})
    outputs = []
    for row in result.get('outputs', []):
        output = Path(row['path'])
        assert output.stat().st_size == row['bytes'] and sha(output) == row['sha256'].upper()
        outputs.append(ref(output))
    cases.append({'scenario': name, 'passed': result['passed'], 'completedTurns': len(result['turns']),
                  'requiredTurns': 8, 'failure': result['failure'], 'assetsAfterClose': 0,
                  'actualNative62Matched': True, 'calls': len(calls), 'failedReceipts': rejected,
                  'terminal': ref(terminal_path), 'result': ref(result_path), 'binding': ref(binding_path),
                  'toolCalls': ref(calls_path), 'captures': captures, 'outputs': outputs})

out = ROOT / 'capability-evidence/diagnostics/af1082-f5-d7-local87cu1/provider-verification.json'
assert not out.exists()
out.write_text(json.dumps({'recordedAt': datetime.now(timezone.utc).isoformat(),
    'state': 'CURRENT_F5_D7_ORIGINAL_PROVIDER_OUTCOMES_ACTUALLY_VERIFIED',
    'freeze': ref(freeze_path), 'parent': ref(DRAFT / 'freeze.json'), 'suite': ref(suite_path),
    'fullGate': ref(ROOT / freeze['parentFullBothAudit']), 'scenarios': cases,
    'originalPromptsAssertionsAndBudgetsChanged': False, 'allPassed': all(c['passed'] for c in cases),
    'productionApplied': False, 'desktopInputStarted': False, 'AFComplete': False,
    'G': 'USER_DEFERRED', 'cleanup': False}, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
print(json.dumps({'scenarios': [{'name': c['scenario'], 'passed': c['passed'],
                               'turns': c['completedTurns']} for c in cases]}))
