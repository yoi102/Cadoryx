"""Receive the exact SDK candidate's terminal evidence without starting native code."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import shutil
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[3]
SDK = ROOT.parent / 'OcctSharp'
OUT = ROOT / 'artifacts/local-sdk87cu1-gates-received-1'
VERSION = '8.0.1-preview.87.cu1'
NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}

def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()
def ref(p): return {'path': p.relative_to(ROOT).as_posix(), 'sha256': sha(p)}

assert not OUT.exists(), 'Preserve previously received evidence.'
delivery_path = SDK / 'OcctSharp/artifacts/batch-cu/planar-wire-candidate-delivery.json'
handoff_path = SDK / 'OcctSharp/artifacts/batch-cu/planar-native-return.json'
delivery, handoff = read(delivery_path), read(handoff_path)
assert delivery['packageVersion'] == handoff['packageVersion'] == VERSION
assert handoff['nativeJobsRunning'] is False and handoff['rootMayStartIndependentExactPackageReception']
assert sha(delivery_path) == handoff['candidateDelivery']['sha256']
qualification = read(SDK / delivery['sourceQualification']['path'])
freeze = read(SDK / delivery['sourceFreeze']['path'])
frozen_root = (SDK / delivery['sourceFreeze']['path']).parent
assert freeze['packageVersion'] == VERSION
assert len(freeze['packageInputs']) == 2431 and len(freeze['qualificationInputs']) == 1841
for row in freeze['packageInputs']:
    assert sha(frozen_root / row['Path']) == row['Sha256']
for row in freeze['qualificationInputs']:
    assert sha(frozen_root / row['path']) == row['sha256']
assert freeze['qualificationInputs'] == qualification['sourceInputs']

received = read(ROOT / 'artifacts/local-sdk87cu1-received-1/receipt.json')
assert received['snapshotSha256'] == freeze['packageInputSnapshotSha256']
assert {(r['id'], r['sha256']) for r in delivery['packages']} == {
    (r['id'], r['sha256']) for r in received['packages']}
native_release = {r['name']: r['sha256'] for r in received['native62']}
files = {}
def collect(v):
    if isinstance(v, dict):
        if 'path' in v and 'sha256' in v:
            p = SDK / v['path']
            assert p.is_file() and sha(p) == v['sha256'], str(p)
            files[v['path']] = p
        for k, value in v.items():
            if k != 'sourceInputs': collect(value)
    elif isinstance(v, list):
        for value in v: collect(value)
collect(delivery)
collect(handoff)
collect(qualification)

def check_tests(row, total):
    tree = ET.parse(SDK / row['path'])
    results = tree.findall('.//t:UnitTestResult', NS)
    counters = tree.find('.//t:Counters', NS).attrib
    assert len(results) == total == int(counters['total']) == int(counters['passed'])
    assert int(counters['failed']) == int(counters['notExecuted']) == 0
    assert all(r.attrib['outcome'] == 'Passed' for r in results)
    assert sorted(r.attrib['testName'] for r in results) == sorted(row['names'])
    assert row['passed'] == total and row['failed'] == row['skipped'] == 0

def check_native(row, release):
    binding = read(SDK / row['path'])
    assert binding['observed'] == binding['expectedPayloadDlls'] == len(binding['modules']) == 62
    assert binding['independentlySelectedSource']
    names = set()
    for module in binding['modules']:
        path, source = Path(module['path']), Path(module['source'])
        assert path.name == source.name and path.name not in names
        names.add(path.name)
        assert sha(path) == sha(source) == module['sha256']
        if release: assert native_release[path.name] == module['sha256']
    expected_names = set(native_release)
    if not release:
        expected_names.remove('tbb12.dll')
        expected_names.add('tbb12_debug.dll')
    assert names == expected_names

for configuration in ['Release', 'Debug']:
    check_tests(qualification['runtime'][configuration], 2367)
    check_tests(qualification['generator'][configuration], 437)
    check_native(qualification['actualNative'][configuration], configuration == 'Release')
for name, consumer in delivery['consumers'].items():
    check_tests(consumer['result'], 53 if name == 'facade' else 31)
    check_native(consumer['native'], True)
    project = ET.parse(SDK / consumer['project']['path'])
    assert not project.findall('.//ProjectReference')
    for package in project.findall('.//PackageReference'):
        if package.attrib['Include'].startswith('OcctSharp'):
            assert package.attrib['Version'] == '[' + VERSION + ']'
    lock = read(SDK / consumer['lock']['path'])
    for framework in lock['dependencies'].values():
        for package, value in framework.items():
            if package.startswith('OcctSharp'):
                assert value['resolved'] == VERSION
                for dep, version in value.get('dependencies', {}).items():
                    if dep.startswith('OcctSharp'): assert version == '[' + VERSION + ']'

OUT.mkdir(parents=True)
for name, path in files.items():
    dest = OUT / 'upstream' / name
    dest.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(path, dest)
    assert sha(dest) == sha(path)
record = {
    'recordedAt': datetime.now(timezone.utc).isoformat(),
    'state': 'EXACT_SDK87CU1_TERMINAL_EVIDENCE_INDEPENDENTLY_RECEIVED',
    'version': VERSION, 'upstreamEvidenceFiles': len(files),
    'evidence': [ref(OUT / 'upstream' / name) for name in sorted(files)],
    'frozenPackageInputsMatched': 2431, 'frozenQualificationInputsMatched': 1841,
    'package14MatchesPreviouslyReceivedFeed': True,
    'actualRuntimeBoth': 2367, 'actualGeneratorBoth': 437,
    'actualFreshFacade': 53, 'actualFreshModules': 31,
    'actualNative62BothAndConsumersHashMatched': True,
    'sourceDebugTbb': 'tbb12_debug.dll; package consumers use Release tbb12.dll',
    'cold418AndApiAdded338Removed0': 'Upstream qualification assertions; linked receipts retained',
    'explicitNativeHandoffReceived': True, 'localConsumerRuntimeStarted': False,
    'productionApplied': False, 'AFComplete': False,
}
(OUT / 'receipt.json').write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
print(json.dumps({k: v for k, v in record.items() if k != 'evidence'}))
