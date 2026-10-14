"""Audit a terminal original workflow against its immutable qualified payload."""
from pathlib import Path
from datetime import datetime, timezone
import argparse, hashlib, json

ROOT = Path(__file__).resolve().parents[2]
STAGE = ROOT / 'artifacts/f5-lmstudio-completion-integrity-1'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref = lambda p: dict(path=p.relative_to(ROOT).as_posix(), sha256=sha(p))
parser = argparse.ArgumentParser()
parser.add_argument('--scenario', choices=('drawing', 'workspace'), required=True)
parser.add_argument('--iteration', type=int, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
folder = STAGE / f'provider-{args.scenario}-{args.iteration}'
terminal_path = folder.with_name(folder.name + '-exit.json')
terminal = read(terminal_path)
qualification_path = Path(terminal['qualification']['path'])
assert sha(qualification_path) == terminal['qualification']['sha256']
qualification = read(qualification_path); candidate = Path(qualification['provider'])
assert candidate == Path(terminal['candidate'])
assert terminal['contextLength'] == 8192 and terminal['maxToolRounds'] == 16 and terminal['originalPerTurnMinutes'] == 4
assert not terminal['desktopInputStarted'] and not terminal['AFComplete']
for row in qualification['providerPayload']: assert sha(candidate / row['path']) == row['sha256']
projection_path = ROOT / qualification['projection']['path']
assert sha(projection_path) == qualification['projection']['sha256']
for row in read(projection_path)['originals'] + [read(projection_path)['projected']]:
    assert sha(ROOT / row['path']) == row['sha256']
result_path, calls_path = folder / 'result.json', folder / 'tool-calls.json'
result, calls = read(result_path), read(calls_path)
assert result['scenario'] == args.scenario and result['provider'] == 'lmstudio'
assert result['model'] == terminal['actualModel'] == 'prism-ml/bonsai-27b'
assert result['endpoint'] == 'http://127.0.0.1:15630/v1'
assert result['actualProductionTools'] and result['assetsAfterClose'] == 0
assert terminal['exitCode'] == (0 if result['passed'] else 1)
assert [r['turn'] for r in result['turns']] == list(range(1, len(result['turns']) + 1))
assert all(r['elapsedMs'] <= 240000 for r in result['turns'])
if result['passed']: assert len(result['turns']) == 8 and result['failure'] is None
else: assert result['failure'] and len(result['turns']) < 8
binding_path = Path(terminal['binding']); binding = read(binding_path)
expected = {r['name']: r['sha256'] for r in qualification['native62']}
assert len(expected) == 62 and {r['name']: r['sha256'] for r in binding['native']} == expected
for row in binding['native']:
    assert Path(row['path']).parent == candidate / 'occt' and sha(Path(row['path'])) == row['sha256']
requests = sorted(folder.glob('request-*.json'))
responses = []
for path in requests:
    request = read(path)
    assert request['Endpoint'] == result['endpoint'] and request['Model'] == result['model']
    assert request['MaxOutputTokens'] == 1024 and request['Temperature'] == 0
for path in sorted(folder.glob('wire-response-*.json')):
    if path.stem.endswith('-status'): continue
    wire = read(path)
    status = read(path.with_name(path.stem + '-status.json'))
    assert status['bodyBytes'] == path.stat().st_size and status['statusCode'] == 200
    choice = wire['choices'][0]; reason = choice.get('finish_reason')
    responses.append(dict(response=ref(path), finishReason=reason, usage=wire.get('usage'),
        contentPresent=bool(choice['message'].get('content')),
        toolCallCount=len(choice['message'].get('tool_calls') or [])))
parsed_calls = []
for path in sorted(folder.glob('response-*.json')):
    if path.stem.endswith('-failure'): continue
    for call in read(path)['ToolCalls']:
        parsed_calls.append(dict(Name=call['Name'], ArgumentsJson=call['ArgumentsJson']))
assert parsed_calls == [dict(Name=c['Name'], ArgumentsJson=c['ArgumentsJson']) for c in calls]
for row in responses:
    if row['finishReason'] == 'length':
        number = Path(row['response']['path']).stem.removeprefix('wire-response-')
        assert not (folder / f'response-{number}.json').exists()
        failure = read(folder / f'response-{number}-failure.json')
        assert 'token limit' in failure['message']
        if qualification_path.name != 'qualification2.json':
            assert failure['type'] == 'Cadoryx.AI.Contracts.AiResponseTruncatedException'
successes, rejected = [], []
for ordinal, call in enumerate(calls, 1):
    receipt = json.loads(call['result']); arguments = json.loads(call['ArgumentsJson'])
    if receipt.get('success') is False:
        rejected.append(dict(ordinal=ordinal, tool=call['Name'], arguments=arguments, receipt=receipt))
    elif receipt.get('success') is True and receipt.get('changed') is True:
        successes.append(dict(ordinal=ordinal, tool=call['Name'], arguments=arguments, receipt=receipt))
out = args.output.resolve()
assert out.is_relative_to(ROOT.resolve()) and not out.exists()
out.parent.mkdir(parents=True, exist_ok=True)
record = dict(recordedAt=datetime.now(timezone.utc).isoformat(),
    state='ORIGINAL_RAW_PROVIDER_TERMINAL_INDEPENDENT_AUDIT', scenario=args.scenario,
    iteration=args.iteration, qualification=ref(qualification_path), terminal=ref(terminal_path),
    result=ref(result_path), calls=ref(calls_path), binding=ref(binding_path),
    passed=result['passed'], completedTurns=len(result['turns']), totalTurns=8,
    failure=result['failure'], assetsAfterClose=0, native62Verified=True,
    originalPromptsAssertions8192_16_4MinutesRetained=True,
    parsedCompleteBatchCallsExactlyMatchDispatchedCalls=True,
    rawResponses=responses, changedSuccessReceipts=successes, rejectedCalls=rejected,
    currentFullSuiteRerun=False, STEP24='EXACT_ORIGINAL_FAILURES_USER_DEFERRED_NOT_RETRIED',
    D6Accepted=False, AFComplete=False)
out.write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
print(f"{args.scenario}{args.iteration}: {len(result['turns'])}/8; {'PASS' if result['passed'] else 'FAIL'}; native62 and complete-batch dispatch verified.")
