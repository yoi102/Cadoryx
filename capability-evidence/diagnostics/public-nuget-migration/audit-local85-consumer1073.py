from pathlib import Path
import base64, datetime, hashlib, json, zipfile

root = Path(__file__).resolve().parents[1]
output = root / 'artifacts/public-occt-preview85-consumer1073-1'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref = lambda p: {'path': str(p), 'sha256': sha(p)}
prep = read(output / 'preparation.json')
terminal = read(output / 'exit.json')
assert terminal['completedStages'] == ['restore', 'restore-locked', 'build-release', 'build-debug']
workspace = Path(prep['workspace'])
for stage in terminal['completedStages']:
    assert read(output / f'{stage}-exit.json')['exitCode'] == 0
for config in ('release', 'debug'):
    text = (output / f'build-{config}.log').read_text(encoding='utf-8-sig')
    assert '0 Warning(s)' in text and '0 Error(s)' in text
source_proof = read(Path(prep['sourceBuild']['path']))
source_root = Path(source_proof['workspace'])
allowed = {'Directory.Build.props', 'NuGet.Config', 'Cadoryx.wpf/runtime-baseline.json'}
changed = []
for row in prep['originalSourcePayload']:
    if sha(workspace / row['path']) != row['sha256']:
        assert row['path'] in allowed or row['path'].endswith('/packages.lock.json')
        changed.append(row['path'])
records = {}
managed = {}
native = {}
for package in prep['packages']:
    archive_path = Path(package['path'])
    assert sha(archive_path) == package['sha256']
    payload = archive_path.read_bytes()
    records[package['id']] = {'sha256': package['sha256'], 'contentHash': base64.b64encode(hashlib.sha512(payload).digest()).decode('ascii')}
    with zipfile.ZipFile(archive_path) as archive:
        for name in archive.namelist():
            if name.startswith('lib/net10.0/') and name.endswith('.dll'):
                managed[Path(name).name] = hashlib.sha256(archive.read(name)).hexdigest().upper()
            if name.startswith('buildTransitive/win-x64/occt/') and name.endswith('.dll'):
                native[Path(name).name] = hashlib.sha256(archive.read(name)).hexdigest().upper()
assert len(records) == 14 and len(managed) == 13 and len(native) == 62
manifest = read(output / 'publisher-runtime-manifest.json')
assert native == {Path(row['path']).name: row['sha256'] for row in manifest['files'] if row['path'].startswith('occt/') and row['path'].endswith('.dll')}
assert native['OcctSharp.Native.dll'] == prep['nativeBridgeSha256']
cache = Path(terminal['freshCache'])
for name, record in records.items():
    directory = cache / name.lower() / prep['version']
    archive = directory / f"{name.lower()}.{prep['version']}.nupkg"
    assert sha(archive) == record['sha256']
    origin = read(directory / '.nupkg.metadata')['source']
    assert Path(origin).resolve() == Path(prep['frozenLocalFeed']).resolve()
locks = []
for relative in (row['path'] for row in prep['originalSourcePayload'] if row['path'].endswith('/packages.lock.json')):
    original, current = read(source_root / relative), read(workspace / relative)
    assert original['dependencies'].keys() == current['dependencies'].keys()
    sdk_count = 0
    for framework, dependencies in current['dependencies'].items():
        old = original['dependencies'][framework]
        old_other = {k: (v.get('type'), v.get('resolved'), v.get('contentHash')) for k, v in old.items() if k != 'OcctSharp' and not k.startswith('OcctSharp.')}
        new_other = {k: (v.get('type'), v.get('resolved'), v.get('contentHash')) for k, v in dependencies.items() if k != 'OcctSharp' and not k.startswith('OcctSharp.')}
        assert old_other == new_other, ('unrelated dependency changed', relative, framework)
        for name, value in dependencies.items():
            if name == 'OcctSharp' or name.startswith('OcctSharp.'):
                assert value['resolved'] == prep['version'] and value['contentHash'] == records[name]['contentHash']
                sdk_count += 1
    locks.append({'path': relative, 'sha256': sha(workspace / relative), 'sdkReferences': sdk_count})
payloads = []
for config in ('Release', 'Debug'):
    for project, framework in (('Cadoryx.wpf','net10.0-windows/win-x64'), ('Cadoryx.Tests','net10.0'), ('Cadoryx.Cli','net10.0')):
        directory = workspace / project / 'bin' / config / framework
        for name, expected in managed.items():
            assert sha(directory / name) == expected
        actual_names = {p.name for p in (directory / 'occt').glob('*.dll')}
        assert actual_names == native.keys()
        for name, expected in native.items():
            assert sha(directory / 'occt' / name) == expected
        payloads.append({'configuration': config, 'project': project, 'directory': str(directory), 'managed13MatchesPackages': True, 'native62MatchesPublisherManifest': True})
usage = read(output / 'compiled-occt-usage.json')
original_usage = read(root / 'capability-evidence/diagnostics/public-nuget-migration/source1073-occt-usage.json')
assert usage['typeCount'] == 221 and usage['memberCount'] == 696 and not usage['directNativeImports']
assert {r['symbol'] for r in usage['types']} == {r['symbol'] for r in original_usage['types']}
assert {r['symbol'] for r in usage['members']} == {r['symbol'] for r in original_usage['members']}
for row in usage['assemblies']:
    assert sha(Path(row['path'])) == row['sha256']
report_path = output / 'verification.json'
assert not report_path.exists()
report_path.write_text(json.dumps({
    'recordedAt': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'state': 'PASS_ISOLATED_SOURCE1073_LOCAL14_BOTH_COMPILES_ALL_ORIGINAL_CODE_AND_ASSERTIONS_SDK13_NATIVE62_DISK_AND221_696_REFERENCE_SETS_RUNTIME_NOT_RUN',
    'version': prep['version'], 'preparation': ref(output / 'preparation.json'), 'terminal': ref(output / 'exit.json'),
    'publisherRuntimeManifest': ref(output / 'publisher-runtime-manifest.json'), 'nativeBridgeSha256': prep['nativeBridgeSha256'],
    'sourcePayloadCount': 1073, 'applicationAndTestCodeOrAssertionsChanged': False, 'changedFiles': changed,
    'unrelatedDependencyVersionsAndHashesChanged': False, 'locks': locks, 'packages': records, 'payloads': payloads,
    'compiledUsage': ref(output / 'compiled-occt-usage.json'), 'actualAssemblyFiles': len(usage['assemblies']),
    'typeCount': 221, 'memberCount': 696, 'originalTypeAndMemberReferenceSetsEqual': True,
    'runtimeExecuted': False, 'publicationConfirmed': False, 'publicSourceOnly': False,
    'vcpkgProfileAccepted': False, 'productionApplied': False, 'cleanup': False, 'AF': 'ACTIVE', 'G': 'USER_DEFERRED'
}, indent=2) + '\n', encoding='utf-8')
print('PASS isolated local SDK85: source1073 unchanged code/assertions; two compile configurations;21 locks;14 packages; all six SDK13/native62 disk outputs; exact221/696 compiled references. Native/public/vcpkg acceptance NOT RUN.')
