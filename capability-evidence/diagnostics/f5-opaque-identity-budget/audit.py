"""Create-once audit of real tokenizer measurements and the retained failures."""
from pathlib import Path
from datetime import datetime, timezone
from collections import Counter
import hashlib, json, shutil, subprocess, xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[3]
STAGE = ROOT / 'artifacts/f5-opaque-identities-1'
OUT = Path(__file__).resolve().parent
assert not (OUT / 'verification.json').exists()
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref = lambda p: dict(path=p.relative_to(ROOT).as_posix(), sha256=sha(p))
cases = [
    ('managed-Release', 'release3/identity-release3.trx', 59, 59),
    ('managed-Debug', 'debug1/identity-debug1.trx', 59, 59),
    ('transport-Release', 'transport-release1/transport-release1.trx', 40, 40),
    ('transport-Debug', 'transport-debug1/transport-debug1.trx', 40, 40),
    ('root-contract-Release', 'contract-release4/contract-release4.trx', 56, 56),
    ('root-contract-Debug', 'contract-debug3/contract-debug3.trx', 56, 56),
    ('old-Agent-same-new3', 'old-agent-baseline1-results/old-agent-baseline1.trx', 3, 2),
    ('old-ViewModel-same-new1', 'old-viewmodel-baseline1-results/old-viewmodel-baseline1.trx', 1, 0),
    ('first-identity-failure', 'release1/identity-release1.trx', 59, 57),
    ('first-contract-failure', 'contract-release1/contract-release1.trx', 31, 30),
    ('default-catalog-failure', 'default-tools1/default-tools1.trx', 1, 0),
    ('recovery-catalog-Release-failure', 'contract-release3/contract-release3.trx', 56, 55),
    ('recovery-catalog-Debug-failure', 'contract-debug2/contract-debug2.trx', 56, 55)]

def results(path):
    tree = ET.parse(path).getroot()
    return tree.findall('.//t:UnitTestResult', {'t': tree.tag.split('}')[0].strip('{')})

rows, names = [], {}
for label, relative, total, passed in cases:
    path = STAGE / relative; actual = results(path)
    assert len(actual) == total and sum(r.attrib['outcome'] == 'Passed' for r in actual) == passed
    assert all(r.attrib['outcome'] in ('Passed', 'Failed') for r in actual)
    names[label] = Counter(r.attrib['testName'] for r in actual)
    copy = OUT / 'records' / relative; copy.parent.mkdir(parents=True, exist_ok=True)
    assert not copy.exists(); shutil.copyfile(path, copy); assert sha(path) == sha(copy)
    rows.append(dict(label=label, total=total, passed=passed, failed=total-passed, skipped=0, trx=ref(copy)))
for label in ('managed', 'root-contract', 'transport'):
    assert names[label+'-Release'] == names[label+'-Debug']
assert sum(v for n,v in names['managed-Release'].items() if n.startswith('Cadoryx.AdapterProbes.')) == 4
fixture = ROOT / 'Cadoryx.Tests/Fixtures/AgentContext/OpaqueIdentityBudget'
measurement = read(fixture / 'tokenizer-measurement.json'); request = read(fixture / 'request-019.json')
native = request['Messages'][5]['Content']; measured = measurement['messages'][5]
assert len(native) == 5680 and measured['actualContentTokens'] == 3002
assert hashlib.sha256(native.encode()).hexdigest().upper() == measured['contentSha256']
assert sha(fixture/'request-019.json') == measurement['source']['sha256'] == sha(ROOT/measurement['source']['path'])
assert measurement['model']['contextLength'] == 8192 and measurement['model']['quantization']['name'] == 'Q1_0'
assert not measurement['inference']
replay = read(STAGE / 'context-replay1.json')
assert len(replay) == 2 and all(r['completeCylinderPreserved'] and r['rowsUnchanged'] and r['MaxOutputTokens'] == 1024 for r in replay)
for row in replay:
    assert row['EstimatedPromptTokens'] <= 8192-1024-(1280 if row['aggressive'] else 640)
    assert row['nativeCharacters'] <= (2400 if row['aggressive'] else 6000)
for name in ('context-replay1.json','old-agent-baseline1-preparation.json','old-viewmodel-baseline1-preparation.json','additional-discovery2.json'):
    assert not (OUT/name).exists(); shutil.copyfile(STAGE/name, OUT/name); assert sha(STAGE/name) == sha(OUT/name)
# The original guide tests have no removed or rewritten lines.
guide = ROOT/'Cadoryx.Tests/DrawingContractRequestGuideTests.cs'; current = guide.read_text(encoding='utf-8-sig')
old = subprocess.check_output(['git','show','HEAD:Cadoryx.Tests/DrawingContractRequestGuideTests.cs'],cwd=ROOT).decode().replace('\r\n','\n')
start = current.index('    [Fact]\n    public async Task EveryAuthorContract'); end = current.index('    [Theory]',start)
assert current[:start] + current[end:] == old
navigation = ROOT/'Cadoryx.Tests/NativeOmittedGeometryReadTests.cs'
assert navigation.read_text(encoding='utf-8-sig') == subprocess.check_output(['git','show','HEAD:Cadoryx.Tests/NativeOmittedGeometryReadTests.cs'],cwd=ROOT).decode().replace('\r\n','\n')
failed = ROOT/'artifacts/af-final87-4/focused-release-1/runtime.trx'; actual = results(failed)
assert len(actual) == 212 and Counter(r.attrib['outcome'] for r in actual) == {'Passed':211,'Failed':1}
shutil.copyfile(failed, OUT/'candidate4-focused-release-failure.trx')
focus = ROOT/'capability-evidence/diagnostics/af-final87/candidate5-focused-verification.json'
assert [r['passed'] for r in read(focus)['configurations']] == [212,212]
sources = ['Cadoryx.Agent/AgentRequestContextBuilder.cs','Cadoryx.Agent/AgentToolResultCompactor.cs',
           'Cadoryx.ViewModels/Toolboxes/CadAgentToolset.cs','Cadoryx.ViewModels/Toolboxes/CadAgentDrawingTools.cs',
           'Cadoryx.ViewModels/Toolboxes/CadDrawingInspection.cs','Cadoryx.Tests/AgentOpaqueIdentityBudgetTests.cs',
           'Cadoryx.Tests/DrawingContractRequestGuideTests.cs','tools/AgentSelectionContextTests/AgentSelectionContextTests.csproj']
record = dict(recordedAt=datetime.now(timezone.utc).isoformat(), state='F5_OPAQUE_IDENTITY_COST_AND_CONTRACT_SUBMISSION_BOTH_FOCUSED_PASS_NEW_FULL_AND_MODEL_PENDING',
    sources=[ref(ROOT/p) for p in sources], fixture=[ref(fixture/p) for p in ('request-019.json','tokenizer-measurement.json')],
    actualTokenizer=dict(characters=5680,actualTokens=3002,oldProseEstimate=1578,inference=False),regressions=rows,
    managedCorePerConfig=55,adapterOnlyPerConfig=4,oldAgentSameThreeTestsTwoPassOneFailure=True,oldViewModelSameOneTestFailure=True,
    originalGuideTestsAdditiveOnly=True,originalNativeNavigationAssertionsUnchanged=True,actualReplay=ref(OUT/'context-replay1.json'),
    replaySummaries=[{k:r[k] for k in ('aggressive','EstimatedPromptTokens','MaxOutputTokens','nativeCharacters','nativeEstimatedTokens','completeCylinderPreserved','rowsUnchanged')} for r in replay],
    candidate4ActualFocused=dict(total=212,passed=211,failed=1,debugStarted=False,trx=ref(OUT/'candidate4-focused-release-failure.trx')),
    candidate5Focus=ref(focus),candidate5Freeze=ref(ROOT/'artifacts/af-final87-5/freeze.json'),actualDiscoveredEach=7198,expectedNonDeferredEach=7174,
    originalSTEP24='DEFERRED_NOT_EXECUTED',rootSDK='8.0.1-preview.87.cu1',rootContractTestsNative62Provenance='NOT_CAPTURED_SEPARATE_FROM_FINAL87_FOCUSED_ACTUAL62',
    fullBothCurrentComplete=False,originalModelReplayed=False,budgetsAndPromptsChanged=False,nativeSDKChanged=False,desktopWindowOrPerformanceRetested=False,AFComplete=False,G='USER_DEFERRED')
(OUT/'verification.json').write_text(json.dumps(record,indent=2)+'\n',encoding='utf-8')
print('Tokenizer/original sources/failures/both focused verified; new full7174 and original model still pending.')
