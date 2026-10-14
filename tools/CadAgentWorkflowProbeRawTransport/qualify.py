"""Qualify the isolated parser delta before original real-provider replays."""
from datetime import datetime, timezone
from pathlib import Path
import hashlib, json, shutil, sys, xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
STAGE = ROOT / 'artifacts/f5-lmstudio-completion-integrity-1'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref = lambda p: dict(path=p.relative_to(ROOT).as_posix(), sha256=sha(p))
base = read(ROOT / 'artifacts/af1082-f5-d7-root-integration-1/source-receipt.json')
client = 'Cadoryx.AI.LmStudio/LmStudioChatClient.cs'
changed = [row['path'] for row in base['rootSource'] if sha(ROOT / row['path']) != row['sha256']]
assert changed == [client]
projection_path = STAGE / 'raw-source1/projection-verification.json'; projection = read(projection_path)
assert projection['originalProgramRecoveredByteExactlyByReversingOnlyTwoRecordingReplacements']
for row in projection['originals'] + [projection['projected']]: assert sha(ROOT / row['path']) == row['sha256']
cases = []
sets = {}
for label, relative, total, passed in [
    ('baseline', 'baseline-results1/baseline.trx', 32, 8),
    ('Release', 'release3/transport-release3.trx', 32, 32),
    ('Debug', 'debug1/transport-debug1.trx', 32, 32),
    ('related-Release', 'related-release1/related-release1.trx', 86, 86),
    ('related-Debug', 'related-debug1/related-debug1.trx', 86, 86)]:
    path = STAGE / relative; tree = ET.parse(path).getroot(); ns={'t':tree.tag.split('}')[0].strip('{')}
    rows=tree.findall('.//t:UnitTestResult', ns)
    assert len(rows)==total and sum(r.attrib['outcome']=='Passed' for r in rows)==passed
    assert all(r.attrib['outcome'] in ('Passed','Failed') for r in rows)
    sets[label]={r.attrib['testName'] for r in rows}
    cases.append(dict(label=label,total=total,passed=passed,failed=total-passed,skipped=0,trx=ref(path)))
assert sets['baseline']==sets['Release']==sets['Debug']
assert sets['related-Release']==sets['related-Debug'] and sets['Release'] <= sets['related-Release']
build = read(STAGE / 'build-exit1.json')
assert [r['exitCode'] for r in build['stages']]==[0,0,1]
logs = [ROOT/r['log'] for r in build['stages'][:2]] + [STAGE/'raw-provider-build2.log']
for path in logs:
    text=path.read_text(encoding='utf-8-sig')
    assert 'Build succeeded.' in text and '0 Warning(s)' in text and '0 Error(s)' in text
old_root = read(ROOT / 'capability-evidence/diagnostics/af1082-f5-d7-local87cu1/root-integration-verification.json')
baseline = Path(read(STAGE/'baseline-exit1.json')['candidate'])
old_client=next(r for r in old_root['payloads'][0]['production'] if r['path'].endswith('/Cadoryx.AI.LmStudio.dll'))
assert sha(baseline/'Cadoryx.AI.LmStudio.dll')==old_client['sha256']
new_tests=ROOT/'tools/LmStudioTransportTests/bin/Release/net10.0'
for path in new_tests.rglob('*'):
    if path.is_file() and path.name != 'Cadoryx.AI.LmStudio.dll':
        assert sha(path)==sha(baseline/path.relative_to(new_tests))
assert not list(new_tests.rglob('OcctSharp*')) and not (new_tests/'occt').exists()
ui_path=ROOT/'artifacts/af1082-f5-d7-ui-culture-1/application-verification2.json'; ui=read(ui_path)
app=Path(ui['candidate']); raw=ROOT/'tools/CadAgentWorkflowProbeRawTransport/bin/Release/net10.0'
for row in ui['payload']: assert sha(app/row['path'])==row['sha256']
iteration=int(sys.argv[1]) if len(sys.argv)==2 else 1
assert 1 <= iteration <= 100
out_app=STAGE/f'application{iteration}'; out_provider=STAGE/f'provider{iteration}'
assert not out_app.exists() and not out_provider.exists()
shutil.copytree(app,out_app)
for name in ('Cadoryx.AI.LmStudio.dll','Cadoryx.AI.LmStudio.pdb'):
    source=ROOT/'Cadoryx.wpf/bin/Release/net10.0-windows/win-x64'/name
    assert sha(source)==sha(raw/name)
    shutil.copyfile(source,out_app/name)
before={r['path']:r['sha256'] for r in ui['payload']}
app_payload=[dict(path=p.relative_to(out_app).as_posix(),sha256=sha(p)) for p in out_app.rglob('*') if p.is_file()]
after={r['path']:r['sha256'] for r in app_payload}
assert before.keys()==after.keys()
assert {n for n in before if before[n]!=after[n]}=={'Cadoryx.AI.LmStudio.dll','Cadoryx.AI.LmStudio.pdb'}
shutil.copytree(raw,out_provider)
for row in ui['payload']:
    # Original18/core/SDK/native/worker/resources from the final UI application,
    # plus only the newly compiled client and unchanged public ABI.
    target=out_provider/row['path']; target.parent.mkdir(parents=True,exist_ok=True)
    shutil.copyfile(out_app/row['path'], target)
provider_payload=[dict(path=p.relative_to(out_provider).as_posix(),sha256=sha(p)) for p in out_provider.rglob('*') if p.is_file()]
freeze=read(ROOT/'artifacts/af1082-f5-d7-local87cu1-1/freeze.json')
for row in freeze['native62']: assert sha(out_provider/'occt'/row['name'])==row['sha256']
out=STAGE/f'qualification{iteration}.json'; assert not out.exists()
record=dict(recordedAt=datetime.now(timezone.utc).isoformat(),
    state='F5_LMSTUDIO_PARSER_ONLY_DELTA_32_BOTH_86_RELATED_BOTH_RAW_PROVIDER_BUILD0_NOT_MODEL_RUNTIME',
    baseRoot=ref(ROOT/'artifacts/af1082-f5-d7-root-integration-1/source-receipt.json'),
    baseUi=ref(ui_path), clientSource=ref(ROOT/client), rootSource1200OnlyClientDiffers=True,
    regression=cases,oldClient=old_client,onlyClientChangedInSame32TestPayload=True,
    builds=[ref(p) for p in logs],preservedRawBuildFailure=ref(STAGE/'raw-provider-build1.log'),
    projection=ref(projection_path), originalPromptsAssertions8192_16_4MinutesRetained=True,
    native62=freeze['native62'],application=str(out_app),appPayload=app_payload,
    provider=str(out_provider),providerPayload=provider_payload,
    original18ProductionDllsSdk13Native62AndWorkerBytesRetainedExceptQualifiedClient=True,
    full7149Rerun=False,originalModelFailureCause='UNDETERMINED_WITHOUT_RAW_RESPONSE',
    actualModelStarted=False,D6Accepted=False,AFComplete=False)
out.write_text(json.dumps(record,indent=2)+'\n',encoding='utf-8')
print('Qualified parser-only app delta and raw-response provider; original32 names/differential24, related86 both, full7149 not rerun.')
