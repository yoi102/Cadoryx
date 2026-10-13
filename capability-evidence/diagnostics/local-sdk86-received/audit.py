from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import shutil
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[3]
SDKROOT = ROOT.parent / 'OcctSharp'
SDK = SDKROOT / 'OcctSharp'
OUT = Path(__file__).resolve().parent
FEED = ROOT / 'artifacts/local-sdk86-received-1/feed'
VERSION = '8.0.1-preview.86'
def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest().upper()
def read(path): return json.loads(path.read_text(encoding='utf-8-sig'))
DELIVERY = SDK / 'artifacts/batch-cq/delivery-result.json'
INPUTS = SDK / f'artifacts/packages/package-inputs.{VERSION}.json'
SOURCE = SDK / 'artifacts/batch-cj/final-source-identities.json'
for path, expected in [
    (DELIVERY, '57DBE296F1AF29B3364D3080B0D982787B064ECEA53A4DBE763AE40070A93161'),
    (INPUTS, '68767DF12046AD9EC4929367AB9E7A526F1C07AFDCACAFE52850B4B539CE6964'),
    (SOURCE, '77786B18F9BDEF3F334C1FA833B21BF19A5E81F4315B78978CBAD56DFBA07D96'),
]: assert sha(path) == expected, path
delivery = read(DELIVERY)
assert delivery['state'] == 'PASS' and delivery['packageVersion'] == VERSION
gates = []
for row in delivery['gates']:
    path = SDKROOT / row['path']
    assert row['state'] == 'PASS' and sha(path) == row['sha256'], row
    gates.append(row)
source = read(SOURCE)
assert len(source['files']) == 2050
for row in source['files']:
    assert sha(SDKROOT / row['path']) == row['sha256'], row['path']
content = read(SDK / 'artifacts/batch-cj/package-content.json')
loaded = read(SDK / 'artifacts/batch-cj/actual-release-loaded.json')
assert loaded['state'] == 'PASS' and loaded['observed'] == 62 and len(loaded['files']) == 62
native_hashes = {r['name']: r['sha256'] for r in loaded['files']}
assert len(native_hashes) == 62
packages = []
managed = []
native = []
licenses = []
for row in content['packages']:
    path = SDK / f'artifacts/packages/{row["id"]}.{VERSION}.nupkg'
    assert sha(path) == row['sha256'] and row['state'] == 'PASS', path
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        nuspecs = [n for n in names if n.endswith('.nuspec')]
        assert len(nuspecs) == 1
        spec = ET.fromstring(archive.read(nuspecs[0]))
        ns = {'n': spec.tag.split('}')[0].removeprefix('{')}
        metadata = spec.find('n:metadata', ns)
        assert metadata.find('n:id', ns).text == row['id']
        assert metadata.find('n:version', ns).text == VERSION
        dependencies = []
        for dep in metadata.findall('.//n:dependency', ns):
            if dep.attrib['id'].startswith('OcctSharp'):
                assert dep.attrib['version'] == f'[{VERSION}]'
                dependencies.append(dep.attrib)
        for name in names:
            if name.startswith('lib/') and name.endswith('.dll'):
                filename = Path(name).name
                original = SDK / f'src/{row["id"]}/bin/Release/net10.0/win-x64/{filename}'
                digest = hashlib.sha256(archive.read(name)).hexdigest().upper()
                assert original.is_file() and sha(original) == digest, original
                managed.append({'package': row['id'], 'entry': name, 'sha256': digest})
            elif name.startswith('buildTransitive/win-x64/occt/') and name.endswith('.dll'):
                digest = hashlib.sha256(archive.read(name)).hexdigest().upper()
                assert native_hashes[Path(name).name] == digest, name
                native.append({'entry': name, 'sha256': digest})
            elif name.startswith('licenses/') and not name.endswith('/'):
                licenses.append({'package': row['id'], 'entry': name,
                                 'sha256': hashlib.sha256(archive.read(name)).hexdigest().upper()})
        packages.append({'id': row['id'], 'path': str(path), 'sha256': row['sha256'], 'dependencies': dependencies})
assert len(packages) == 14 and len(managed) == 13 and len(native) == 62
assert len(licenses) == 11
assert next(r for r in native if Path(r['entry']).name == 'OcctSharp.Native.dll')['sha256'] == '045EF9E158FB6C9A74CB238E1BA183629E9430D889666DC57E6076010EF7AA23'
assert sha(SDK / 'artifacts/native/Debug/OcctSharp.Native.dll') == '78E9414027599D5E7CEB378F0FE73093A8BA2E56A681076B95D898EAAC86D5AA'
FEED.mkdir(parents=True, exist_ok=False)
for row in packages:
    path = Path(row['path']); dest = FEED / path.name
    shutil.copyfile(path, dest); assert sha(dest) == row['sha256']
result = {'recordedAt': datetime.now(timezone.utc).isoformat(),
          'state': 'LOCAL86_DELIVERY_FILES_SOURCES_PACKAGES_VERIFIED_NOT_CADORYX_CONSUMPTION',
          'version': VERSION, 'publiclyPublished': False, 'productionApplied': False,
          'sourceFilesMatched': 2050, 'gatesMatched': len(gates), 'packages': packages,
          'managed13': managed, 'native62': native, 'licenseEntriesObserved': licenses,
          'immutableReceivedFeed': str(FEED), 'deliveryPath': str(DELIVERY), 'deliverySha256': sha(DELIVERY),
          'inputManifestPath': str(INPUTS), 'inputManifestSha256': sha(INPUTS),
          'sourceFreezePath': str(SOURCE), 'sourceFreezeSha256': sha(SOURCE),
          'nativeRuntimeStartedHere': False, 'CadoryxFinalGate': 'NOT RUN',
          'priorColdWindowFailures': 'Preserved; repeat passed, cause not proved; no viewer repair claim',
          'AF': 'ACTIVE', 'G': 'USER_DEFERRED', 'cleanup': False}
target = OUT / 'verification.json'
assert not target.exists()
target.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'state': result['state'], 'sourceFiles': 2050, 'gates': len(gates),
                  'packages': len(packages), 'managed': len(managed), 'native': len(native), 'feed': str(FEED)}))
