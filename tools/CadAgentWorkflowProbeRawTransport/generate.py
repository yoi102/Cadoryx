"""Project the unchanged original workflows with raw HTTP response recording."""
from pathlib import Path
import datetime
import hashlib
import json
import sys

ROOT = Path(__file__).resolve().parents[2]
original = ROOT / 'tools/CadAgentWorkflowProbe/Program.cs'
folder = Path(sys.argv[1]).resolve()
assert folder.is_relative_to((ROOT / 'artifacts').resolve())
assert not folder.exists(), 'Preserve every previous projection.'
old = original.read_bytes()
newline = b'\r\n' if b'\r\n' in old else b'\n'
wire_anchor = b'        return await base.SendAsync(request,token);'
wire_replacement = '''        var response=await base.SendAsync(request,token);
        if(request.Content is not null)
        {
            // This fixture handles only its owned synthetic workflow. Header
            // credentials/cookies are excluded; preserve actual response bytes.
            var body=await response.Content.ReadAsByteArrayAsync(token);
            await File.WriteAllBytesAsync(Path.Combine(output,$"wire-response-{number:D3}.json"),body,token);
            await File.WriteAllTextAsync(Path.Combine(output,$"wire-response-{number:D3}-status.json"),
                JsonSerializer.Serialize(new{statusCode=(int)response.StatusCode,bodyBytes=body.Length}),token);
        }
        return response;'''.replace('\n', newline.decode()).encode()
chat_anchor = b'        var result=await real.CompleteAsync(request,token);await File.WriteAllTextAsync(Path.Combine(output,$"response-{index:D3}.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}),token);return result;'
chat_replacement = '''        try
        {
            var result=await real.CompleteAsync(request,token);
            await File.WriteAllTextAsync(Path.Combine(output,$"response-{index:D3}.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}),token);
            return result;
        }
        catch(Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(output,$"response-{index:D3}-failure.json"),
                JsonSerializer.Serialize(new{type=error.GetType().FullName,message=error.Message}),CancellationToken.None);
            throw;
        }'''.replace('\n', newline.decode()).encode()
assert old.count(wire_anchor) == old.count(chat_anchor) == 1
projected = old.replace(wire_anchor, wire_replacement).replace(chat_anchor, chat_replacement)
assert projected.replace(wire_replacement, wire_anchor).replace(chat_replacement, chat_anchor) == old
folder.mkdir()
path = folder / 'Program.cs'; path.write_bytes(projected)
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
sources = [original, ROOT / 'tools/CadAgentWorkflowProbe/DrawingProviderProbe.cs', ROOT / 'tools/CadAgentWorkflowProbe/WorkspaceProviderProbe.cs']
frozen = json.loads((ROOT / 'artifacts/af1082-f5-d7-local87cu1-1/followup-preparation.json').read_text(encoding='utf-8-sig'))
for row in frozen['originalModelPromptAndAssertionSources']: assert sha(ROOT / row['path']) == row['sha256']
record = dict(recordedAt=datetime.datetime.now(datetime.timezone.utc).isoformat(),
    state='ORIGINAL_PROVIDER_WORKFLOWS_RAW_RESPONSE_RECORDING_PROJECTION_NOT_RUNTIME',
    originals=[dict(path=p.relative_to(ROOT).as_posix(), sha256=sha(p)) for p in sources],
    projected=dict(path=path.relative_to(ROOT).as_posix(), sha256=sha(path)),
    originalProgramRecoveredByteExactlyByReversingOnlyTwoRecordingReplacements=True,
    originalPromptsAssertionsModel8192Context16Rounds4MinutesUnchanged=True,
    originalTransportRequestBytesNotModified=True, headersRecorded=False,
    rawResponsesSyntheticOwnedScopeOnly=True, liveProviderStarted=False, AFComplete=False)
(folder / 'projection-verification.json').write_text(json.dumps(record, indent=2)+'\n',encoding='utf-8')
print('Projected original provider workflows; only raw response/error recording changed, reverse byte comparison passed.')
