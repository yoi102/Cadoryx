"""Persist managed-only evidence; do not imply root/model/native acceptance."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib, json, shutil, xml.etree.ElementTree as ET
from collections import Counter

ROOT = Path(__file__).resolve().parents[2]
STAGE = ROOT / 'artifacts/f5-native-selection-context-1'
OUT = ROOT / 'capability-evidence/diagnostics/f5-native-selection-context'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref = lambda p: dict(path=p.relative_to(ROOT).as_posix(), sha256=sha(p))
rows = []
sets = {}
for label, relative, total, passed in [
    ('Release', 'release6/selection-release6.trx', 49, 49),
    ('Debug', 'debug4/selection-debug4.trx', 49, 49),
    ('old-Agent-same-payload', 'baseline5-results/baseline5.trx', 2, 0),
    ('transport-Release', 'transport-release3/transport-release8.trx', 40, 40),
    ('transport-Debug', 'transport-debug3/transport-debug6.trx', 40, 40)]:
    path = STAGE / relative
    tree = ET.parse(path).getroot(); ns = {'t': tree.tag.split('}')[0].strip('{')}
    results = tree.findall('.//t:UnitTestResult', ns)
    assert len(results) == total and sum(r.attrib['outcome'] == 'Passed' for r in results) == passed
    assert all(r.attrib['outcome'] in ('Passed', 'Failed') for r in results)
    names = Counter(r.attrib['testName'] for r in results)
    sets[label] = names
    rows.append(dict(label=label, total=total, passed=passed, failed=total-passed, skipped=0, trx=ref(path)))
assert sets['Release'] == sets['Debug'] and sets['old-Agent-same-payload'] <= sets['Release']
assert sets['transport-Release'] == sets['transport-Debug']
assert sum(count for n, count in sets['Release'].items() if n.startswith('Cadoryx.AdapterProbes.')) == 4
assert sum(count for n, count in sets['Release'].items() if n.startswith('Cadoryx.Tests.NativeCompleteSourceContextTests.')) == 13
assert sum(count for n, count in sets['Release'].items() if n.startswith('Cadoryx.Tests.AgentNativeSelectionRetentionTests.')) == 32
payload = ROOT / 'tools/AgentSelectionContextTests/bin/Release/net10.0'
baseline = Path(read(STAGE / 'baseline5-exit.json')['candidate'])
assert read(STAGE / 'baseline5-exit.json')['exitCode'] == 1
for path in payload.rglob('*'):
    if path.is_file() and path.name != 'Cadoryx.Agent.dll':
        assert sha(path) == sha(baseline / path.relative_to(payload))
assert sha(baseline / 'Cadoryx.Agent.dll') == sha(ROOT / 'artifacts/f5-lmstudio-completion-integrity-1/application4/Cadoryx.Agent.dll')
for config in ('Release', 'Debug'):
    selection = ROOT / f'tools/AgentSelectionContextTests/bin/{config}/net10.0'
    transport = ROOT / f'tools/LmStudioTransportTests/bin/{config}/net10.0'
    for name in ('Cadoryx.Agent.dll', 'Cadoryx.AI.Contracts.dll'):
        assert sha(selection / name) == sha(transport / name)
    assert not list(selection.rglob('OcctSharp*')) and not (selection / 'occt').exists()
fixture_path = ROOT / 'Cadoryx.Tests/Fixtures/AgentContext/NativeSelectionRetention/before-request30.json'
fixture = read(fixture_path)
assert fixture['AssistantAndToolMessagesCopiedFromActualCompletedResponsesWithoutRewriting']
for source in fixture['Sources']: assert sha(ROOT / source['path']) == source['sha256']
assert read(STAGE / 'adapter-exact1.json')['allRemainingSiblingNamesPreserved']
assert read(STAGE / 'exact-filter2.json')['siblingCasesPreserved']
assert len(read(STAGE / 'filter-audit-guards1.json')['negativeGuards']) == 3
OUT.mkdir(parents=True, exist_ok=True)
copies = [ROOT / r['trx']['path'] for r in rows] + [STAGE / p for p in (
    'baseline5-exit.json', 'exact-filter2.json', 'exact-filter2/exact-filter2.trx',
    'adapter-exact1.json', 'adapter-all1/adapter-all1.trx', 'adapter-exact1/adapter-exact1.trx',
    'filter-audit-guards1.json')]
for source in copies:
    target = OUT / 'records' / source.relative_to(STAGE)
    target.parent.mkdir(parents=True, exist_ok=True)
    assert not target.exists(); shutil.copyfile(source, target)
    assert sha(target) == sha(source)
sources = [ROOT / p for p in (
    'Cadoryx.Agent/AgentNativeSelectionReceipt.cs', 'Cadoryx.Agent/AgentRequestContextBuilder.cs',
    'Cadoryx.Agent/AgentSuccessfulActionReceipt.cs', 'Cadoryx.Agent/AgentToolResultCompactor.cs',
    'Cadoryx.Tests/AgentNativeSelectionRetentionTests.cs',
    'tools/AgentSelectionContextTests/AgentSelectionContextTests.csproj',
    'tools/CurrentCadCandidate/deferred-step-filter.ps1')]
out = OUT / 'verification.json'; assert not out.exists()
record = dict(recordedAt=datetime.now(timezone.utc).isoformat(),
    state='MANAGED_NATIVE_READ_RETENTION_45_CORE_PLUS4_ADAPTER_BOTH_TRANSPORT40_BOTH_NOT_PRODUCT_ACCEPTANCE',
    regressions=rows, coreNewTests=32, existingNativePageTests=13, adapterOnlyTests=4,
    sameFinalTestPayloadOnlyAgentSwappedBaselineTwoFailures=True,
    sharedAgentAndContractsPayloadVerifiedBoth=True, standaloneNoOcctPayload=True,
    sources=[ref(p) for p in sources], fixture=ref(fixture_path),
    preservedEarlierFailureLog=ref(ROOT / 'artifacts/f5-native-selection-context-baseline1.log'),
    exactSTEP24ExclusionPreparedAndAdapterVerified=True, currentRootDiscovery=False,
    full7192BuiltDiscoveredOrRun=False, full7192ExpectedOnly=True,
    originalModelReplayedThisTurn=False, nativeWindowOrPerformanceStarted=False,
    rootWaitingForFinalDAExplicitReturn=True, STEP24Retried=False, D6Accepted=False,
    AFComplete=False, G='USER_DEFERRED', cleanup=False)
out.write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
print('Managed core45+adapter4 both/transport40 both; same-payload old-Agent actual replay0/2; full7192/model/native NOT RUN.')
