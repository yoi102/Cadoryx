"""Receive exact 87.cu1 archives; this does not execute or qualify a consumer."""
from pathlib import Path, PurePosixPath
from datetime import datetime, timezone
import argparse
import hashlib
import json
import shutil
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[2]
VERSION = '8.0.1-preview.87.cu1'
parser = argparse.ArgumentParser()
parser.add_argument('--snapshot', type=Path, required=True)
parser.add_argument('--packages', type=Path, required=True)
parser.add_argument('--source-root', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()

def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()

def digest(data): return hashlib.sha256(data).hexdigest().upper()

snapshot_path = args.snapshot.resolve()
source_root = args.source_root.resolve()
packages_root = args.packages.resolve()
output = args.output.resolve()
assert not output.exists(), 'Preserve any previous reception evidence.'
snapshot = json.loads(snapshot_path.read_text(encoding='utf-8-sig'))
assert snapshot['state'] == 'PASS' and snapshot['packageVersion'] == VERSION
assert len(snapshot['packages']) == 14 and snapshot['nativeDlls'] == 62
assert len({r['name'] for r in snapshot['packages']}) == 14
for row in snapshot['sourceInputs']:
    path = (source_root / row['Path']).resolve()
    assert path.is_relative_to(source_root), 'Source identity escapes checkout.'
    assert sha(path) == row['Sha256']

packages = []
managed = []
native = []
ids = set()
for row in snapshot['packages']:
    name = row['name']
    assert Path(name).name == name and name.endswith('.' + VERSION + '.nupkg')
    path = packages_root / name
    assert sha(path) == row['sha256']
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        assert len(names) == len(set(names)), 'Duplicate package entry.'
        for entry in names:
            p = PurePosixPath(entry)
            assert not p.is_absolute() and '..' not in p.parts and '\\' not in entry
        specs = [n for n in names if n.endswith('.nuspec')]
        assert len(specs) == 1
        spec = ET.fromstring(archive.read(specs[0]))
        def find_text(tag):
            return next(n.text for n in spec.iter() if n.tag.rsplit('}', 1)[-1] == tag)
        package_id = find_text('id')
        assert package_id not in ids and package_id.startswith('OcctSharp')
        ids.add(package_id)
        assert find_text('version') == VERSION
        for node in spec.iter():
            if node.tag.rsplit('}', 1)[-1] == 'dependency' and node.attrib['id'].startswith('OcctSharp'):
                assert node.attrib['version'] == '[' + VERSION + ']'
        for entry in names:
            if entry.startswith('lib/net10.0/') and entry.endswith('.dll'):
                managed.append({'package': package_id, 'entry': entry,
                                'name': PurePosixPath(entry).name, 'sha256': digest(archive.read(entry))})
            if entry.startswith('buildTransitive/win-x64/occt/') and entry.endswith('.dll'):
                native.append({'package': package_id, 'entry': entry,
                               'name': PurePosixPath(entry).name, 'sha256': digest(archive.read(entry))})
        licenses = [n for n in spec.iter() if n.tag.rsplit('}', 1)[-1] == 'license']
        assert len(licenses) == 1 and licenses[0].text, 'Package license metadata absent.'
        if licenses[0].attrib.get('type') == 'file':
            assert licenses[0].text in names, 'Declared license file absent.'
        else:
            assert licenses[0].attrib.get('type') == 'expression'
    packages.append({'id': package_id, 'name': name, 'sha256': row['sha256']})
assert len(managed) == len({r['name'] for r in managed}) == 13
assert len(native) == len({r['name'] for r in native}) == 62
assert 'OcctSharp.dll' in {r['name'] for r in managed}
assert 'OcctSharp.Native.dll' in {r['name'] for r in native}

# Mutate only after the complete source/ZIP/identity checks succeed.
output.mkdir(parents=True)
feed = output / 'feed'
feed.mkdir()
for row in packages:
    shutil.copyfile(packages_root / row['name'], feed / row['name'])
    assert sha(feed / row['name']) == row['sha256']
assert len(list(feed.iterdir())) == 14
record = {'recordedAt': datetime.now(timezone.utc).isoformat(),
          'state': 'EXACT_LOCAL87CU1_PACKAGE_BYTES_AND_SOURCE_RECEIVED_NOT_CONSUMER_RUNTIME',
          'version': VERSION, 'snapshotPath': str(snapshot_path), 'snapshotSha256': sha(snapshot_path),
          'sourceRoot': str(source_root), 'sourceFilesMatched': len(snapshot['sourceInputs']),
          'packages': packages, 'managed13': managed, 'native62': native,
          'immutableReceivedFeed': str(feed), 'nativeRuntimeStartedHere': False,
          'upstreamDeliveryGatesIndependentlyReceived': False,
          'productionApplied': False, 'publiclyPublished': False, 'AFComplete': False}
(output / 'receipt.json').write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'version': VERSION, 'packages': 14, 'managed': 13, 'native': 62,
                  'sourceFiles': len(snapshot['sourceInputs']), 'consumerRuntime': False}))
