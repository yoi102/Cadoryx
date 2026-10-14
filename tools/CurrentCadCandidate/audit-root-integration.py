"""Audit applied D7 source, root builds and original guard replay separately."""
from datetime import datetime, timezone
from pathlib import Path
import hashlib
import json
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
STAGE = ROOT / 'artifacts/af1082-f5-d7-root-integration-1'
DRAFT = ROOT / 'artifacts/af1082-f5-d7-local87cu1-1'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref = lambda p: dict(path=p.relative_to(ROOT).as_posix(), sha256=sha(p))

def check(reference):
    path = ROOT / reference['path']; assert sha(path) == reference['sha256']
    return path

source_path = STAGE / 'source-receipt.json'; source = read(source_path)
freeze = read(check(source['candidate']))
assert freeze['version'] == '8.0.1-preview.87.cu1'
assert source['sourceApplied'] and source['existingProvenUiCultureDeltaPreserved']
assert len(source['rootSource']) == 1200 and len(source['changedPaths']) == 38
assert len([n for n in source['changedPaths'] if n.endswith('/packages.lock.json')]) == 30
for row in source['rootSource']: assert sha(ROOT / row['path']) == row['sha256']
for reference in source['prerequisites']: check(reference)
build_path = STAGE / 'build-exit1.json'; build = read(build_path)
assert build['rootBuildProven'] and [r['stage'] for r in build['stages']] == ['restore', 'Release', 'Debug']
for row in build['stages']:
    assert row['exitCode'] == 0
    log = check(dict(path=row['log'], sha256=row['sha256']))
    if row['stage'] != 'restore':
        text = log.read_text(encoding='utf-8-sig')
        assert 'Build succeeded.' in text and '0 Warning(s)' in text and '0 Error(s)' in text
payloads = []
for configuration in ('Release', 'Debug'):
    frozen = next(r for r in freeze['configurations'] if r['configuration'] == configuration)
    app = ROOT / f'Cadoryx.wpf/bin/{configuration}/net10.0-windows/win-x64'
    tests = ROOT / f'Cadoryx.Tests/bin/{configuration}/net10.0'
    sdk = [r for r in frozen['appPayload'] if r['path'].startswith('OcctSharp') and r['path'].endswith('.dll') and '/' not in r['path']]
    assert len(sdk) == 13
    for folder in (app, tests):
        for row in sdk: assert sha(folder / row['path']) == row['sha256']
        for row in freeze['native62']: assert sha(folder / 'occt' / row['name']) == row['sha256']
    production = [r for r in frozen['appPayload'] if r['path'].startswith('Cadoryx.') and r['path'].endswith('.dll') and '/' not in r['path']]
    assert len(production) == 19
    payloads.append(dict(configuration=configuration, sdk13= sdk, native62MatchedInWpfAndTests=True,
        production=[ref(app / r['path']) for r in production], tests=ref(tests / 'Cadoryx.Tests.dll'),
        exactFrozenProductionDllBytesMatched=all(sha(app / r['path']) == r['sha256'] for r in production)))
runtime = STAGE / 'binding36-release-3'
terminal_path = runtime / 'exit.json'; terminal = read(terminal_path)
assert terminal['exitCode'] == 0 and terminal['actualNative62'] and terminal['planned'] == 36
assert not terminal['newAssertions'] and not terminal['STEP24Run']
trx_path = runtime / 'result.trx'; trx = ET.parse(trx_path).getroot()
ns = {'t': trx.tag.split('}')[0].strip('{')}
rows = trx.findall('.//t:UnitTestResult', ns)
assert len(rows) == 36 and all(r.attrib['outcome'] == 'Passed' for r in rows)
full = read(ROOT / 'capability-evidence/diagnostics/af1082-f5-d7-local87cu1/full-verification.json')
full_release = next(r for r in full['configurations'] if r['configuration'] == 'Release')
base = ET.parse(check(full_release['trx'])).getroot()
prefix = 'Cadoryx.Tests.FiniteSurfaceAnalyticBranchTests.MissingSelectedHalfCannotManufactureMaterialOrChangeHistory'
expected = {r.attrib['testName'] for r in base.findall('.//t:UnitTestResult', ns) if r.attrib['testName'].startswith(prefix)}
assert len(expected) == 36 and {r.attrib['testName'] for r in rows} == expected
binding_path = runtime / 'loaded-native.json'; binding = read(binding_path)
assert len(binding['native']) == 62
assert {r['name']: r['sha256'] for r in binding['native']} == {r['name']: r['sha256'] for r in freeze['native62']}
for row in binding['native']:
    assert Path(row['path']).parent == ROOT / 'Cadoryx.Tests/bin/Release/net10.0/occt'
    assert sha(Path(row['path'])) == row['sha256']
assert (runtime / 'stderr.log').stat().st_size == 0
tree_path = runtime / 'owned-process-tree.json'; tree = read(tree_path)
assert tree['nativeHostPid'] == binding['processId'] and binding['processName'] == 'testhost'
parents = {r['ProcessId']: r['ParentProcessId'] for r in tree['ownedProcessTree']}
pid = tree['nativeHostPid']; seen = set()
while pid != tree['coordinatorPid']:
    assert pid not in seen; seen.add(pid); pid = parents[pid]
revision = re.compile(rb'1\.0\.0\+([a-f0-9]{40})')
root_revision = revision.search((ROOT / 'Cadoryx.wpf/bin/Release/net10.0-windows/win-x64/Cadoryx.Db.dll').read_bytes()).group(1).decode()
frozen_release = next(r for r in freeze['configurations'] if r['configuration'] == 'Release')
frozen_revision = revision.search((Path(frozen_release['candidate']) / 'Cadoryx.Db.dll').read_bytes()).group(1).decode()
assert frozen_revision == read(DRAFT / 'preparation.json')['baseHead']
out = ROOT / 'capability-evidence/diagnostics/af1082-f5-d7-local87cu1/root-integration-verification.json'
assert not out.exists()
record = dict(recordedAt=datetime.now(timezone.utc).isoformat(),
    state='D7_EXACT_LOCAL87CU1_ROOT_SOURCE_AND_BOTH_BUILD0_SDK13_NATIVE62_RELEASE_GUARD36_VERIFIED',
    source=ref(source_path), build=ref(build_path), source1200MatchedWithOnlyProvenWpfCultureDelta=True,
    payloads=payloads, originalReleaseGuard36=dict(trx=ref(trx_path), terminal=ref(terminal_path),
        binding=ref(binding_path), processTree=ref(tree_path), passed=36, failed=0, skipped=0,
        originalFull7117NamesRetained=True), originalFrozenFullBoth7117=ref(ROOT / 'capability-evidence/diagnostics/af1082-f5-d7-local87cu1/full-verification.json'),
    rootBuildGitRevision=root_revision, originalFrozenBuildGitRevision=frozen_revision,
    rootAndFrozenProductionDllByteEquality='NOT_CLAIMED; normal root builds embed the current Git revision, frozen assemblies embed the earlier revision; sources and SDK/native payloads verified separately.',
    rootFull7117Rerun=False, retainedFailedRunner1=ref(STAGE / 'binding36-release-1/exit.json'),
    retainedUncapturedBindingRunner2=ref(STAGE / 'binding36-release-2/exit.json'),
    rootProductionRuntimeBindingProven='RELEASE_TESTHOST_NATIVE62_ONLY',
    D6AndMaterialDesktopAcceptance=False, providerAllPassed=False, STEP24='USER_DEFERRED_FAILURES_NOT_RETRIED',
    publiclyPublished=False, AFComplete=False, G='USER_DEFERRED', cleanup=False)
out.write_text(json.dumps(record, indent=2)+'\n',encoding='utf-8')
print('Verified exact root1200 source, both build0, SDK13/native62 disk identity and actual root Release guard36/native62; root full7117 was not rerun.')
