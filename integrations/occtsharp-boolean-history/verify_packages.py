"""Verify the local NuGet family, native payload and every Cadoryx lock reference."""
from pathlib import Path
import base64
import hashlib
import json
import subprocess
import xml.etree.ElementTree as ET
import zipfile

root = Path(__file__).resolve().parents[2]
packages = Path(r'C:\Users\yoiri\source\repos\OcctSharp\OcctSharp\artifacts\packages')
version = ET.parse(root / 'Directory.Build.props').getroot().findtext('.//OcctSharpVersion')
expected_native = json.loads((root / 'Cadoryx.wpf/runtime-baseline.json').read_text(encoding='utf-8'))['nativeSha256']
records = {}
for path in packages.glob(f'*.{version}.nupkg'):
    payload = path.read_bytes()
    with zipfile.ZipFile(path) as archive:
        nuspec = ET.fromstring(archive.read(next(n for n in archive.namelist() if n.endswith('.nuspec'))))
        meta = next(e for e in nuspec if e.tag.endswith('metadata'))
        name = next(e.text for e in meta if e.tag.endswith('id'))
        actual_version = next(e.text for e in meta if e.tag.endswith('version'))
        assert actual_version == version, (name, actual_version)
        for e in meta.iter():
            if e.tag.endswith('dependency') and e.attrib['id'].startswith('OcctSharp'):
                assert e.attrib['version'].strip('[]') == version, (name, e.attrib)
        if name == 'OcctSharp.Native.win-x64':
            dlls = [n for n in archive.namelist() if n.lower().endswith('.dll')]
            assert len(dlls) == 62
            bridge = next(n for n in dlls if n.endswith('/OcctSharp.Native.dll'))
            assert hashlib.sha256(archive.read(bridge)).hexdigest() == expected_native
        if name == 'OcctSharp':
            doc = archive.read('docs/BOOLEAN_TOPOLOGY_HISTORY.md').decode('utf-8')
            assert 'Repair::Copy' in doc and '5/5' in doc and version in doc
    records[name] = dict(sha256=hashlib.sha256(payload).hexdigest(),
                         contentHash=base64.b64encode(hashlib.sha512(payload).digest()).decode('ascii'))
assert len(records) == 14, len(records)
lock_count = 0
for name in subprocess.check_output(['rg', '--files', '-g', 'packages.lock.json'], cwd=root, text=True).splitlines():
    lock = json.loads((root / name).read_text(encoding='utf-8'))
    for group in lock['dependencies'].values():
        for package, data in group.items():
            if package.startswith('OcctSharp'):
                assert data['resolved'] == version, (name, package, data)
                assert data['contentHash'] == records[package]['contentHash'], (name, package)
                lock_count += 1
report = dict(passed=True, version=version, nativeSha256=expected_native, packages=records, lockReferences=lock_count)
(root / 'artifacts/h2b2-package-audit.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
print(f'14 NuGet packages, 62 native DLLs and {lock_count} lock references verified: {version}')
