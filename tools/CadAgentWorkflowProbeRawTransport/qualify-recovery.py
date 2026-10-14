"""Freeze the bounded recovery delta; preserve every earlier parser-only run."""
from datetime import datetime, timezone
from pathlib import Path
import hashlib, json, shutil, sys, xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
STAGE = ROOT / 'artifacts/f5-lmstudio-completion-integrity-1'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref = lambda p: dict(path=p.relative_to(ROOT).as_posix(), sha256=sha(p))
base_path = ROOT / 'artifacts/af1082-f5-d7-root-integration-1/source-receipt.json'
expected = {
    'Cadoryx.AI.Contracts/AiChatContracts.cs', 'Cadoryx.AI.LmStudio/LmStudioChatClient.cs',
    'Cadoryx.Agent/AgentContracts.cs', 'Cadoryx.Agent/AgentRunner.cs',
    'Cadoryx.Lang/Strings/Strings.Designer.cs', 'Cadoryx.Lang/Strings/Strings.resx',
    'Cadoryx.Lang/Strings/Strings.zh-CN.resx', 'Cadoryx.Lang/Strings/Strings.ja-JP.resx',
    'Cadoryx.ViewModels/Toolboxes/AiAssistantToolboxViewModel.cs',
    'Cadoryx.Tests/AgentEmptyResponseRecoveryTests.cs'}
changed = {r['path'] for r in read(base_path)['rootSource'] if sha(ROOT / r['path']) != r['sha256']}
assert changed == expected, (changed - expected, expected - changed)
sources = [ref(ROOT / p) for p in sorted(expected | {'Cadoryx.Tests/LmStudioCompletionIntegrityTests.cs'})]
projection_path = STAGE / 'raw-source1/projection-verification.json'
projection = read(projection_path)
assert projection['originalProgramRecoveredByteExactlyByReversingOnlyTwoRecordingReplacements']
for row in projection['originals'] + [projection['projected']]:
    assert sha(ROOT / row['path']) == row['sha256']
cases, sets = [], {}
for label, relative, total, passed in [
    ('baseline-client', 'baseline-results3/baseline3.trx', 40, 8),
    ('Release', 'release5/transport-release5.trx', 40, 40),
    ('Debug', 'debug3/transport-debug3.trx', 40, 40),
    ('related-Release', 'related-release4/related-release4.trx', 97, 97),
    ('related-Debug', 'related-debug4/related-debug4.trx', 97, 97)]:
    path = STAGE / relative
    tree = ET.parse(path).getroot(); ns = {'t': tree.tag.split('}')[0].strip('{')}
    rows = tree.findall('.//t:UnitTestResult', ns)
    assert len(rows) == total and sum(r.attrib['outcome'] == 'Passed' for r in rows) == passed
    assert all(r.attrib['outcome'] in ('Passed', 'Failed') for r in rows)
    sets[label] = {r.attrib['testName'] for r in rows}
    cases.append(dict(label=label, total=total, passed=passed, failed=total-passed, skipped=0, trx=ref(path)))
assert sets['baseline-client'] == sets['Release'] == sets['Debug']
assert sets['related-Release'] == sets['related-Debug'] and sets['Release'] <= sets['related-Release']
logs = [STAGE / p for p in ('root-release3.log', 'root-debug3.log', 'raw-provider-build5.log')]
for path in logs:
    text = path.read_text(encoding='utf-8-sig')
    assert 'Build succeeded.' in text and '0 Warning(s)' in text and '0 Error(s)' in text
baseline = Path(read(STAGE / 'baseline-exit3.json')['candidate'])
old_client = Path(read(STAGE / 'baseline-exit1.json')['candidate']) / 'Cadoryx.AI.LmStudio.dll'
assert read(STAGE / 'baseline-exit3.json')['exitCode'] == 1
assert sha(baseline / old_client.name) == sha(old_client)
new_tests = ROOT / 'tools/LmStudioTransportTests/bin/Release/net10.0'
assert not list(new_tests.rglob('OcctSharp*')) and not (new_tests / 'occt').exists()
for path in new_tests.rglob('*'):
    if path.is_file() and path.name != old_client.name:
        assert sha(path) == sha(baseline / path.relative_to(new_tests))
ui_path = ROOT / 'artifacts/af1082-f5-d7-ui-culture-1/application-verification2.json'
ui = read(ui_path); app = Path(ui['candidate'])
for row in ui['payload']: assert sha(app / row['path']) == row['sha256']
raw = ROOT / 'tools/CadAgentWorkflowProbeRawTransport/bin/Release/net10.0'
production = ROOT / 'Cadoryx.wpf/bin/Release/net10.0-windows/win-x64'
replace = {f'{name}.{ext}' for name in (
    'Cadoryx.AI.Contracts', 'Cadoryx.AI.LmStudio', 'Cadoryx.Agent',
    'Cadoryx.Lang', 'Cadoryx.ViewModels') for ext in ('dll', 'pdb')}
replace |= {'ja-JP/Cadoryx.Lang.resources.dll', 'zh-CN/Cadoryx.Lang.resources.dll'}
assert all(sha(production / name) == sha(raw / name) for name in replace)
iteration = int(sys.argv[1]) if len(sys.argv) == 2 else 4
assert 4 <= iteration <= 100
out_app, out_provider = STAGE / f'application{iteration}', STAGE / f'provider{iteration}'
out = STAGE / f'qualification{iteration}.json'
assert not any(p.exists() for p in (out_app, out_provider, out))
shutil.copytree(app, out_app)
for name in replace: shutil.copyfile(production / name, out_app / name)
app_payload = [dict(path=p.relative_to(out_app).as_posix(), sha256=sha(p)) for p in out_app.rglob('*') if p.is_file()]
before = {r['path']: r['sha256'] for r in ui['payload']}
after = {r['path']: r['sha256'] for r in app_payload}
assert before.keys() == after.keys()
assert {n for n in before if before[n] != after[n]} == replace
shutil.copytree(raw, out_provider)
for row in ui['payload']:
    target = out_provider / row['path']; target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(out_app / row['path'], target)
provider_payload = [dict(path=p.relative_to(out_provider).as_posix(), sha256=sha(p)) for p in out_provider.rglob('*') if p.is_file()]
freeze = read(ROOT / 'artifacts/af1082-f5-d7-local87cu1-1/freeze.json')
for row in freeze['native62']: assert sha(out_provider / 'occt' / row['name']) == row['sha256']
record = dict(recordedAt=datetime.now(timezone.utc).isoformat(),
    state='F5_BOUNDED_TRUNCATION_RECOVERY_40_BOTH_97_RELATED_BOTH_RAW_PROVIDER_BUILD0_NOT_MODEL_RUNTIME',
    baseRoot=ref(base_path), baseUi=ref(ui_path), qualifiedSources=sources,
    rootSource1200Differences=sorted(changed), regression=cases,
    onlyOldClientSwappedInSame40TestPayload=True, builds=[ref(p) for p in logs],
    preservedRawBuildFailures=[ref(STAGE / p) for p in ('raw-provider-build1.log', 'raw-provider-build3.log')],
    projection=ref(projection_path), originalPromptsAssertions8192_16_4MinutesRetained=True,
    singleRecoveryAllowanceSharedWithEmptyReply=True, partialToolBatchDispatch=False,
    native62=freeze['native62'], application=str(out_app), appPayload=app_payload,
    provider=str(out_provider), providerPayload=provider_payload, replacedPayloadPaths=sorted(replace),
    unchangedCoreSdkNativeWorkerBytesRetained=True, full7160Rerun=False,
    actualModelStarted=False, D6Accepted=False, AFComplete=False)
out.write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
print('Qualified recovery delta: 40/40 and related97/97 both; same40 old-client baseline8/32; full7160 not rerun.')
