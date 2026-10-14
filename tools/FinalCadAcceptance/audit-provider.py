"""Audit actual provider results and raw HTTP captures for the final87 build."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
iteration = int(sys.argv[1])
assert 1 <= iteration <= 100
STAGE = ROOT / f'artifacts/af-final87-{iteration}'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref = lambda p: dict(path=p.relative_to(ROOT).as_posix(), sha256=sha(p))
subprocess.run([sys.executable, str(Path(__file__).with_name('audit-full.py')), str(iteration)], check=True)
freeze, prep, suite = (read(STAGE / p) for p in ('freeze.json', 'followup-preparation.json', 'after-full-exit.json'))
assert suite['modelsTerminal'] and [r['scenario'] for r in suite['scenarios']] == ['drawing', 'workspace']
assert prep['freeze']['sha256'] == sha(STAGE / 'freeze.json')
assert (prep['model'], prep['variant'], prep['endpoint'], prep['context'], prep['toolRounds'], prep['perTurnMinutes']) == (
    'prism-ml/bonsai-27b', 'prism-ml/bonsai-27b@q1_0', 'http://127.0.0.1:15630/v1', 8192, 16, 4)
candidate = Path(freeze['provider'])
for row in freeze['providerPayload']:
    assert sha(candidate / row['path']) == row['sha256']
for row in prep['models']:
    assert sha(ROOT / row['path']) == row['sha256']
projection = read(STAGE / 'preparation.json')['rawProjection']
assert projection['originalProgramRecoveredByteExactlyByReversingOnlyTwoRecordingReplacements']
assert sha(Path(freeze['workspace']) / 'tools/CadAgentWorkflowProbeRawTransport/RecordedProgram.cs') == projection['projected']['sha256']
expected_native = {r['name']: r['sha256'] for r in freeze['native62']}
cases = []
for gate in suite['scenarios']:
    scenario = gate['scenario']
    terminal_path = Path(gate['terminal'])
    assert sha(terminal_path) == gate['sha256']
    terminal = read(terminal_path)
    folder = STAGE / f'provider-{scenario}-1'
    result_path, calls_path = folder / 'result.json', folder / 'tool-calls.json'
    result, calls = read(result_path), read(calls_path)
    start_path = Path(str(folder) + '-start.json')
    start = read(start_path)
    assert start['freezeSHA256'] == sha(STAGE / 'freeze.json')
    actual = start['actualModel']
    assert actual['identifier'] == prep['model'] and actual['selectedVariant'] == prep['variant'] and actual['contextLength'] == 8192
    assert result['scenario'] == terminal['scenario'] == scenario and result['provider'] == 'lmstudio'
    assert result['model'] == prep['model'] and result['endpoint'] == prep['endpoint']
    assert result['actualProductionTools'] and result['assetsAfterClose'] == 0
    assert terminal['exitCode'] == gate['exitCode'] == (0 if result['passed'] else 1)
    assert not terminal['desktopInputStarted'] and terminal['context'] == 8192 and terminal['toolRounds'] == 16 and terminal['perTurnMinutes'] == 4
    assert 0 <= len(result['turns']) <= 8
    assert [r['turn'] for r in result['turns']] == list(range(1, len(result['turns']) + 1))
    if result['passed']:
        assert len(result['turns']) == 8 and result['failure'] is None
    else:
        assert result['failure']
    binding_path = Path(terminal['binding'])
    binding = read(binding_path)
    assert len(binding['native']) == 62 and {r['name']: r['sha256'] for r in binding['native']} == expected_native
    for row in binding['native']:
        assert Path(row['path']).parent == candidate / 'occt' and sha(Path(row['path'])) == row['sha256']
    rejected = []
    for ordinal, call in enumerate(calls, 1):
        receipt = json.loads(call['result'])
        if receipt.get('success') is False:
            rejected.append(dict(ordinal=ordinal, tool=call['Name'], arguments=json.loads(call['ArgumentsJson']), receipt=receipt))
    requests = sorted(folder.glob('request-*.json'))
    assert requests and len(requests) == len(list(folder.glob('wire-request-*.json')))
    captures = []
    for ordinal, request in enumerate(requests, 1):
        assert request.name == f'request-{ordinal:03}.json'
        wire = folder / f'wire-request-{ordinal:03}.json'
        raw = folder / f'wire-response-{ordinal:03}.json'
        status = folder / f'wire-response-{ordinal:03}-status.json'
        decoded = folder / f'response-{ordinal:03}.json'
        client_failure = folder / f'response-{ordinal:03}-failure.json'
        # A typed client rejection can follow a complete HTTP response. An
        # interrupted or missing HTTP response never qualifies as a capture.
        assert decoded.exists() != client_failure.exists()
        metadata = read(status)
        assert metadata['statusCode'] == 200 and metadata['bodyBytes'] == raw.stat().st_size
        wire_data, response = read(wire), read(raw)
        assert wire_data['model'] == prep['model'] and response['choices']
        if client_failure.exists():
            failure = read(client_failure)
            assert failure['type'] and failure['message']
        captures.append(dict(request=ref(request), wire=ref(wire), rawResponse=ref(raw), httpStatus=ref(status),
                             decoded=ref(decoded) if decoded.exists() else None,
                             typedClientFailure=ref(client_failure) if client_failure.exists() else None))
    outputs = []
    for row in result.get('outputs', []):
        path = Path(row['path'])
        assert path.stat().st_size == row['bytes'] and sha(path) == row['sha256'].upper()
        outputs.append(ref(path))
    cases.append(dict(scenario=scenario, passed=result['passed'], completedTurns=len(result['turns']), requiredTurns=8,
                      failure=result['failure'], assetsAfterClose=0, actualNative62Matched=True, calls=len(calls),
                      failedReceipts=rejected, terminal=ref(terminal_path), start=ref(start_path), result=ref(result_path),
                      binding=ref(binding_path), toolCalls=ref(calls_path), captures=captures, outputs=outputs))
out = ROOT / f'capability-evidence/diagnostics/af-final87/candidate{iteration}-provider-verification.json'
record = dict(recordedAt=datetime.now(timezone.utc).isoformat(), state='FINAL87_ORIGINAL_PROVIDER_ACTUAL_OUTCOMES_RAW_CAPTURE_VERIFIED',
              freeze=ref(STAGE / 'freeze.json'), suite=ref(STAGE / 'after-full-exit.json'), scenarios=cases,
              allPassed=all(c['passed'] for c in cases), originalPromptsAssertionsAndBudgetsChanged=False,
              productionApplied=False, desktopInputStarted=False, AFComplete=False)
if out.exists():
    before = read(out)
    assert {k: v for k, v in before.items() if k != 'recordedAt'} == {k: v for k, v in record.items() if k != 'recordedAt'}
else:
    out.write_text(json.dumps(record, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
print(json.dumps(dict(scenarios=[dict(name=c['scenario'], passed=c['passed'], turns=c['completedTurns']) for c in cases])))
