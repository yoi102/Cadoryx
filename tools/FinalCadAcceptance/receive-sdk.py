"""Independently receive exact final87 packages after the explicit SDK return."""
from pathlib import Path
from datetime import datetime, timezone
from collections import Counter
import hashlib, json, shutil, xml.etree.ElementTree as ET, zipfile

ROOT = Path(__file__).resolve().parents[2]
SDK = Path('C:/Users/yoiri/source/repos/OcctSharp')
STAGE = ROOT / 'artifacts/af-final87-1'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
def check(row):
    path = Path(row['path'])
    if not path.is_absolute(): path = SDK / path
    assert sha(path) == row['sha256'], path
    return path
returned = ROOT / 'artifacts/af1082-f5-d7-root-integration-1/sdk-final-da-return-received1.json'
accept = read(returned); assert accept['sdkExplicitNativeReturnReceived'] and accept['nativeConsumersActuallyIdle']
confirmation = check(accept['sdkConfirmation']); receipt = read(confirmation)
assert receipt['explicitlyReturned'] and receipt['rootMayResumeNativeWindowModelPerformance']
assert sha(Path(receipt['sourceGrant']['path'])) == receipt['sourceGrant']['sha256'] == accept['parentGrantSHA256']
qualified_path = check(receipt['qualification']); qualified = read(qualified_path)
supplement_path = check(receipt['supplement']); supplement = read(supplement_path)
assert qualified['packageVersion'] == receipt['finalPackageVersion'] == '8.0.1-preview.87'
assert qualified['nativeAbi'] == '1.119' and qualified['bridgeVersion'] == '0.134.0' and qualified['newManual'] == 0
tests = []
for config, suites in qualified['fullSuites'].items():
    for count, row in suites.items():
        path = check(row); tree = ET.parse(path).getroot(); ns = {'t': tree.tag.split('}')[0].strip('{')}
        results = tree.findall('.//t:UnitTestResult', ns)
        assert len(results) == int(count) == row['passed'] and all(r.attrib['outcome'] == 'Passed' for r in results)
        assert Counter(r.attrib['testName'] for r in results) == Counter(row['testNames'])
        tests.append(dict(configuration=config, count=int(count), path=str(path), sha256=sha(path)))
assert len(tests) == 8
bindings = []
for config, rows in qualified['actualNative'].items():
    selected = read(check(rows['source'])); observed = read(check(rows['binding']))
    wanted = {r['name']: r['sha256'] for r in selected}; assert len(wanted) == 62
    assert observed['state'] == 'PASS_ACTUALLY_OBSERVED_MODULES' and observed['observed'] == 62
    actual = {Path(r['path']).name: r['sha256'] for r in observed['modules']}
    assert wanted == actual
    for row in selected: assert sha(Path(row['copy'])) == row['sha256']
    for row in observed['modules']: assert sha(Path(row['source'])) == row['sha256']
    bindings.append(dict(configuration=config, expected=wanted, observedReference=rows['binding']))
for key in ('sourceFreeze','cold','releaseEntry','localGates','exports','extraChecks'): check(qualified[key])
freeze_path = check(qualified['packages']); freeze = read(freeze_path)
assert len(freeze['packages']) == 14 and len(freeze['expectedNative62']) == 62
assert sha(freeze_path.parent / 'package-inputs.json') == freeze['packageInputsSha256']
for row in freeze['sourceInputs']: assert sha(SDK / row['copy']) == row['sha256']
managed, native, package_rows = {}, {}, []
for row in freeze['packages']:
    path = freeze_path.parent / row['name']; assert sha(path) == row['sha256']
    with zipfile.ZipFile(path) as archive:
        for name in archive.namelist():
            if not name.endswith('.dll'): continue
            if name.startswith('lib/net10.0/'):
                target = managed
            elif name.startswith(('runtimes/win-x64/native/', 'buildTransitive/win-x64/occt/')):
                target = native
            else: continue
            key = Path(name).name; digest = hashlib.sha256(archive.read(name)).hexdigest().upper()
            assert key not in target or target[key] == digest
            target[key] = digest
    package_rows.append(dict(name=row['name'], sha256=row['sha256']))
assert len(managed) == 13 and native == freeze['expectedNative62']
assert dict(next(b for b in bindings if b['configuration'] == 'Release')['expected']) == native
for consumer, row in qualified['consumers'].items():
    path = check(row['result']); tree = ET.parse(path).getroot(); ns = {'t': tree.tag.split('}')[0].strip('{')}
    results = tree.findall('.//t:UnitTestResult', ns)
    assert len(results) == row['result']['passed'] and all(r.attrib['outcome'] == 'Passed' for r in results)
    observed = read(check(row['binding'])); assert observed['observed'] == 62
    assert {Path(r['path']).name: r['sha256'] for r in observed['modules']} == native
    check(row['lock'])
assert supplement['publicConsumers']['noFriendAssembly'] and supplement['publicConsumers']['noProjectReferences']
check(supplement['rawAbiPackageReplay']['result']); check(supplement['rawAbiPackageReplay']['actualNative'])
assert supplement['rawAbiPackageReplay']['passed'] == 5
STAGE.mkdir(parents=True, exist_ok=False)
feed = STAGE / 'feed'; feed.mkdir()
for row in package_rows: shutil.copyfile(freeze_path.parent / row['name'], feed / row['name']); assert sha(feed / row['name']) == row['sha256']
for name, path in [('sdk-return.json',confirmation), ('sdk-qualification.json',qualified_path), ('sdk-supplement.json',supplement_path), ('sdk-package-freeze.json',freeze_path)]:
    shutil.copyfile(path, STAGE / name)
out = dict(recordedAt=datetime.now(timezone.utc).isoformat(), state='FINAL87_SDK_EXACT_LOCAL_PACKAGES_RECEIVED_APPLICATION_NOT_RUN',
    version='8.0.1-preview.87', nativeAbi='1.119', bridgeVersion='0.134.0',
    explicitReturn=dict(path=str(confirmation), sha256=sha(confirmation)), qualification=dict(path=str(qualified_path),sha256=sha(qualified_path)),
    supplement=dict(path=str(supplement_path),sha256=sha(supplement_path)), feed=str(feed), packages=package_rows,
    managed13=[dict(name=n,sha256=h) for n,h in sorted(managed.items())], native62=[dict(name=n,sha256=h) for n,h in sorted(native.items())],
    sdkFullSuites=tests, sdkActualNative=bindings, originalPrivateABISeparate=True,
    independentColdBindingsVerifiedAgainstTheirOwnPayloadNotPackageByteEquality=True,
    applicationAccepted=False, publicNuget=False, AFComplete=False, STEP24Retried=False)
(STAGE / 'sdk-reception.json').write_text(json.dumps(out,indent=2)+'\n',encoding='utf-8')
print('Received exact14 final87 packages/managed13/native62; SDK eight full TRX and four actual bindings verified; application NOT RUN.')
