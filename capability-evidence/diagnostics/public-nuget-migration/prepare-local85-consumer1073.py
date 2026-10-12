from pathlib import Path
import datetime, hashlib, json, shutil, zipfile, xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref = lambda p: {'path': str(p), 'sha256': sha(p)}
source_proof = root / 'artifacts/d6-finite-newbody-draft-4/build-both-1-verification.json'
source = read(source_proof)
source_root = Path(source['workspace'])
assert len(source['sourcePayload']) == 1073
output = root / 'artifacts/public-occt-preview85-consumer1073-1'
assert not output.exists()
workspace = output / 'workspace'
workspace.mkdir(parents=True)
originals = []
for row in source['sourcePayload']:
    relative = Path(row['path'])
    assert not relative.is_absolute() and '..' not in relative.parts
    origin, target = source_root / relative, workspace / relative
    assert sha(origin) == row['sha256']
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(origin, target)
    assert sha(target) == row['sha256']
    originals.append({'path': row['path'], 'sha256': row['sha256']})
version = '8.0.1-preview.85'
bridge = 'D7AF1A2118EE5C13AC789EAFDB85EE264009F291953C0987BC4E4B90255F9E02'
publisher_root = Path('C:/Users/yoiri/source/repos/OcctSharp/OcctSharp')
manifest_source = publisher_root / 'runtime/win-x64/runtime-manifest.json'
manifest_copy = output / 'publisher-runtime-manifest.json'
shutil.copy2(manifest_source, manifest_copy)
manifest = read(manifest_copy)
assert manifest['packageVersion'] == version and manifest['nativeAbi'] == '1.118' and manifest['bridgeVersion'] == '0.133.0'
native = {Path(row['path']).name: row['sha256'] for row in manifest['files'] if row['path'].startswith('occt/') and row['path'].endswith('.dll')}
assert len(native) == 62 and native['OcctSharp.Native.dll'] == bridge
family = {'OcctSharp','OcctSharp.Runtime','OcctSharp.Foundation','OcctSharp.Geometry','OcctSharp.MeshData','OcctSharp.Modeling','OcctSharp.Mesh','OcctSharp.Documents','OcctSharp.Visualization','OcctSharp.DataExchange','OcctSharp.Xde','OcctSharp.IVtk','OcctSharp.Draw','OcctSharp.Native.win-x64'}
feed = output / 'frozen-local-candidate-feed'
feed.mkdir()
packages = []
for name in sorted(family):
    original = publisher_root / 'artifacts/packages' / f'{name}.{version}.nupkg'
    target = feed / original.name
    shutil.copy2(original, target)
    assert sha(original) == sha(target)
    with zipfile.ZipFile(target) as archive:
        nuspec = ET.fromstring(archive.read(next(n for n in archive.namelist() if n.endswith('.nuspec'))))
        metadata = next(e for e in nuspec if e.tag.endswith('metadata'))
        assert next(e.text for e in metadata if e.tag.endswith('id')) == name
        assert next(e.text for e in metadata if e.tag.endswith('version')) == version
        if name == 'OcctSharp.Native.win-x64':
            actual = {Path(n).name: hashlib.sha256(archive.read(n)).hexdigest().upper() for n in archive.namelist() if n.startswith('buildTransitive/win-x64/occt/') and n.endswith('.dll')}
            assert actual == native
    packages.append({'id': name, **ref(target)})
props_path = workspace / 'Directory.Build.props'
props = props_path.read_bytes()
old_version = ET.fromstring(props).findtext('.//OcctSharpVersion')
assert old_version == '8.0.1-preview.31.cadoryx.exact-step.31'
assert props.count(old_version.encode()) == 1
props_path.write_bytes(props.replace(old_version.encode(), version.encode()))
config_path = workspace / 'NuGet.Config'
config_path.write_text(f'''<?xml version="1.0" encoding="utf-8"?>
<configuration>
 <packageSources><clear/><add key="frozen-local-candidate" value="{feed.as_posix()}"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
 <packageSourceMapping><packageSource key="frozen-local-candidate"><package pattern="OcctSharp"/><package pattern="OcctSharp.*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>
''', encoding='utf-8')
baseline_path = workspace / 'Cadoryx.wpf/runtime-baseline.json'
baseline = read(baseline_path)
baseline.update(occtSharpPackage=version, nativeSha256=bridge.lower(), nativeAbi=manifest['nativeAbi'], bridgeVersion=manifest['bridgeVersion'])
baseline_path.write_text(json.dumps(baseline, indent=2) + '\n', encoding='utf-8')
changed = {row['path'] for row in originals if sha(workspace / row['path']) != row['sha256']}
assert changed == {'Directory.Build.props', 'NuGet.Config', 'Cadoryx.wpf/runtime-baseline.json'}
(output / 'preparation.json').write_text(json.dumps({
    'recordedAt': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'state': 'ISOLATED_SOURCE1073_LOCAL_PACKAGE85_PREPARED_NO_REMOTE_RESTORE_OR_RUNTIME_ACCEPTANCE',
    'version': version, 'sourceBuild': ref(source_proof), 'workspace': str(workspace),
    'originalSourcePayload': originals, 'changedFiles': sorted(changed), 'packages': packages,
    'frozenLocalFeed': str(feed), 'publisherRuntimeManifest': ref(manifest_copy),
    'nativeBridgeSha256': bridge, 'nativeDlls': len(native),
    'applicationAndTestCodeOrAssertionsChanged': False, 'productionApplied': False,
    'runtimeExecuted': False, 'publicationConfirmed': False, 'vcpkgProfileAccepted': False,
    'cleanup': False, 'AF': 'ACTIVE', 'G': 'USER_DEFERRED'
}, indent=2) + '\n', encoding='utf-8')
print('PASS isolated source1073; only SDK version/config/runtime identity changed; frozen local14/native62 bytes. Restore/build/runtime NOT RUN.')
