"""Copy actual user turns, accepted responses and receipts before request30."""
from pathlib import Path
import json, hashlib
ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'artifacts/f5-lmstudio-completion-integrity-1/provider-drawing-3'
OUT = ROOT / 'Cadoryx.Tests/Fixtures/AgentContext/NativeSelectionRetention'
assert not OUT.exists()
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
ref = lambda p: dict(path=p.relative_to(ROOT).as_posix(), sha256=hashlib.sha256(p.read_bytes()).hexdigest().upper())
calls = read(SOURCE / 'tool-calls.json')
messages, references, cursor, previous = [], [ref(SOURCE / 'tool-calls.json')], 0, None
for number in range(1, 30):
    request_path = SOURCE / f'request-{number:03}.json'
    response_path = SOURCE / f'response-{number:03}.json'
    request, response = read(request_path), read(response_path)
    user = [m for m in request['Messages'] if m['Role'] == 1][-1]
    if user != previous:
        messages.append(user); previous = user
    messages.append(dict(Role=2, Content=response['Content'], ToolCalls=response['ToolCalls']))
    for call in response['ToolCalls']:
        actual = calls[cursor]; cursor += 1
        assert actual['Name'] == call['Name'] and actual['ArgumentsJson'] == call['ArgumentsJson']
        messages.append(dict(Role=3, Content=actual['result'], ToolCallId=call['Id']))
    references += [ref(request_path), ref(response_path)]
final_path = SOURCE / 'request-030.json'; final = read(final_path)
assert cursor == 27 and [m for m in messages if m['Role'] == 1][-1] == [m for m in final['Messages'] if m['Role'] == 1][-1]
assert all(m['Role'] in (1, 2, 3) for m in messages)
native = [m for m in messages if m['Role'] == 3 and read(SOURCE / 'tool-calls.json')[19]['result'] == m['Content']]
assert len(native) == 1
item = next(i for i in json.loads(native[0]['Content'])['items'] if i.get('geometry') == 'CylinderFace')
record = dict(SystemPrompt=final['Messages'][0]['Content'], ContextWindowTokens=8192,
    Messages=messages, Tools=final['Tools'], NativeReadToolCallId=native[0]['ToolCallId'],
    ExpectedCylinderItem=item, OriginalMaximumToolRounds=16, OriginalPerTurnMinutes=4,
    AssistantAndToolMessagesCopiedFromActualCompletedResponsesWithoutRewriting=True,
    Sources=references + [ref(final_path)])
OUT.mkdir(parents=True)
(OUT / 'before-request30.json').write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
print('Copied actual completed exchanges before request30; original prompts/receipts unchanged; no model/native execution.')
