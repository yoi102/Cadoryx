"""Verify current completed provider evidence without running a model or native code."""
from pathlib import Path
import hashlib
import json
import runpy

ROOT = Path(__file__).resolve().parents[2]
DRAFT = ROOT / 'artifacts/af1082-f5-d7-local87cu1-1'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
def check(reference, expected=None):
    p = ROOT / reference['path']; assert sha(p) == reference['sha256']
    if expected is not None: assert p.resolve() == expected.resolve()

runpy.run_path(str(Path(__file__).with_name('require-full-gate.py')))
proof = read(ROOT / 'capability-evidence/diagnostics/af1082-f5-d7-local87cu1/provider-verification.json')
assert proof['state'] == 'CURRENT_F5_D7_ORIGINAL_PROVIDER_OUTCOMES_ACTUALLY_VERIFIED'
check(proof['parent'], DRAFT / 'freeze.json')
check(proof['freeze'], DRAFT / 'provider-runtime-freeze.json')
check(proof['suite'], DRAFT / 'provider-both-exit.json')
check(proof['fullGate'], ROOT / 'capability-evidence/diagnostics/af1082-f5-d7-local87cu1/full-verification.json')
freeze, provider = read(DRAFT / 'freeze.json'), read(DRAFT / 'provider-runtime-freeze.json')
candidate = Path(provider['candidate']); assert candidate == Path(freeze['provider'])
for row in provider['payload']: assert sha(candidate / row['path']) == row['sha256']
assert provider['model']['identifier'] == 'prism-ml/bonsai-27b'
assert provider['model']['selectedVariant'] == 'prism-ml/bonsai-27b@q1_0'
assert provider['model']['contextLength'] == provider['originalContext'] == 8192
assert provider['originalToolRounds'] == 16 and provider['originalPerTurnMinutes'] == 4
assert provider['endpoint'] == 'http://127.0.0.1:15630/v1'
for row in provider['originalPromptAndAssertionSources']: assert sha(ROOT / row['path']) == row['sha256']
assert len(proof['scenarios']) == 2 and {r['scenario'] for r in proof['scenarios']} == {'drawing', 'workspace'}
expected = {r['name']: r['sha256'] for r in freeze['native62']}
for case in proof['scenarios']:
    for key in ('terminal', 'result', 'binding', 'toolCalls'): check(case[key])
    result, terminal, binding = (read(ROOT / case[key]['path']) for key in ('result', 'terminal', 'binding'))
    assert result['scenario'] == case['scenario'] and result['passed'] == case['passed']
    assert result['model'] == terminal['model'] == provider['model']['identifier']
    assert result['provider'] == terminal['provider'] == 'lmstudio'
    assert result['endpoint'] == terminal['endpoint'] == provider['endpoint']
    assert result['assetsAfterClose'] == case['assetsAfterClose'] == 0 and result['actualProductionTools']
    assert terminal['exitCode'] == (0 if result['passed'] else 1)
    assert len(result['turns']) == case['completedTurns'] <= case['requiredTurns'] == 8
    if result['passed']: assert len(result['turns']) == 8 and result['failure'] is None
    else: assert result['failure'] == case['failure']
    assert len(binding['native']) == 62 and {r['name']: r['sha256'] for r in binding['native']} == expected
    for row in binding['native']:
        assert Path(row['path']).parent == candidate / 'occt' and sha(Path(row['path'])) == row['sha256']
    for capture in case['captures']:
        for reference in capture.values(): check(reference)
assert proof['allPassed'] == all(r['passed'] for r in proof['scenarios'])
print(json.dumps({'actualBothOriginalProviderOutcomesVerified': True, 'allPassed': proof['allPassed'],
                  'modelOrNativeCodeStarted': False}))
