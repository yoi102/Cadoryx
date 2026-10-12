from pathlib import Path
import argparse, base64, datetime, hashlib, json, zipfile, xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--directory', default=str(root / 'artifacts/public85-final1073-1'))
parser.add_argument('--current-production', action='store_true')
args = parser.parse_args()
output = Path(args.directory)
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref = lambda p: {'path': str(p), 'sha256': sha(p)}
prep, terminal = read(output / 'preparation.json'), read(output / 'exit.json')
public = read(output / 'public-restore-verification.json')
assert sha(output / 'public-restore-verification.json') == terminal['publicVerificationSha256']
assert public['state'] == 'PASS_FRESH_PUBLIC_ONLY_14_PACKAGE_LOCKED_RESTORE_EXACT_NATIVE_DISK_PAYLOAD_RUNTIME_NOT_RUN'
assert public['version'] == prep['version'] and public['source'] == 'https://api.nuget.org/v3/index.json'
workspace, cache = Path(prep['workspace']), Path(terminal['freshCache'])
support = read(output / 'support-inputs.json')
for row in support['files']:
    assert sha(Path(row['origin'])) == row['sha256']
    assert sha(workspace / row['path']) == row['sha256']
stages = terminal['stages']
assert terminal['sdkVersion'] == '10.0.401'
assert len(stages) == 120 and len({(r['target'], r['stage']) for r in stages}) == 120
assert len({r['target'] for r in stages}) == 30 and all(r['exitCode'] == 0 for r in stages)
for stage in stages:
    if stage['stage'] in ('Release', 'Debug'):
        text = Path(stage['log']).read_text(encoding='utf-8-sig')
        assert '0 Warning(s)' in text and '0 Error(s)' in text, stage
covered = {e.attrib['Path'] for e in ET.parse(workspace / 'Cadoryx.slnx').iter('Project')}
covered |= {r['target'] for r in stages if r['target'] != 'Cadoryx.slnx'}
assert covered == set(prep['projects']) and len(covered) == 50
resolved_sources = []
for project in prep['projects']:
    assets_path = (workspace / project).parent / 'obj/project.assets.json'
    assets = read(assets_path)
    assert set(assets['project']['restore']['sources']) == {public['source']}, project
    resolved_sources.append({'project':project,'assetsSha256':sha(assets_path),'sources':list(assets['project']['restore']['sources'])})
sources = ET.parse(workspace / 'NuGet.Config').findall('./packageSources/add')
assert len(sources) == 1 and sources[0].attrib['value'] == public['source']
assert ET.parse(workspace / 'Directory.Build.props').findtext('.//OcctSharpVersion') == public['version']
packages, managed, native = {}, {}, {}
for package in public['packages']:
    name = package['id']
    assert name not in packages and package['version'] == public['version']
    folder = cache / name.lower() / public['version']
    archive_path = folder / f"{name.lower()}.{public['version']}.nupkg"
    assert read(folder / '.nupkg.metadata')['source'] == public['source']
    payload = archive_path.read_bytes()
    assert base64.b64encode(hashlib.sha512(payload).digest()).decode('ascii') == package['archiveSha512']
    assert sha(archive_path) == package['sha256']
    packages[name] = {'contentHash': package['contentHash'], **ref(archive_path)}
    with zipfile.ZipFile(archive_path) as archive:
        for entry in archive.namelist():
            if entry.startswith('lib/net10.0/') and entry.endswith('.dll'):
                key = Path(entry).name
                assert key not in managed
                managed[key] = hashlib.sha256(archive.read(entry)).hexdigest().upper()
            if entry.startswith('buildTransitive/win-x64/occt/') and entry.endswith('.dll'):
                key = Path(entry).name
                assert key not in native
                native[key] = hashlib.sha256(archive.read(entry)).hexdigest().upper()
assert len(packages) == 14 and len(managed) == 13
assert native == {r['name']: r['sha256'] for r in public['native']}
baseline = read(workspace / 'Cadoryx.wpf/runtime-baseline.json')
assert baseline['occtSharpPackage'] == public['version']
assert baseline['nativeAbi'] == public['nativeAbi'] and baseline['bridgeVersion'] == public['bridgeVersion']
assert baseline['nativeSha256'].upper() == native['OcctSharp.Native.dll']

allowed = {*prep['changedFiles'], 'Cadoryx.wpf/runtime-baseline.json'}
changed, locks, added = [], [], []
parent_io = read(workspace / 'Cadoryx.IO/packages.lock.json')['dependencies']['net10.0']
for row in prep['originalInputFiles']:
    original, current = Path(row['origin']), workspace / row['path']
    assert sha(original) == row['sha256'], ('original frozen input changed', row['path'])
    if sha(current) != row['sha256']:
        assert row['path'] in allowed or row['path'].endswith('/packages.lock.json'), row['path']
        changed.append(row['path'])
    if not row['path'].endswith('/packages.lock.json'):
        continue
    old, new = read(original)['dependencies'], read(current)['dependencies']
    assert old.keys() == new.keys()
    count = 0
    for framework, dependencies in new.items():
        for name, value in dependencies.items():
            if name == 'OcctSharp' or name.startswith('OcctSharp.'):
                assert value['resolved'] == public['version'] and value['contentHash'] == packages[name]['contentHash']
                count += 1
            elif value.get('type') != 'Project':
                if name in old[framework]:
                    before = old[framework][name]
                    assert (value.get('resolved'),value.get('contentHash')) == (before.get('resolved'),before.get('contentHash'))
                else:
                    assert row['path'] in ('tools/SketchProbe/packages.lock.json','tools/StorageBench/packages.lock.json')
                    assert name in ('PDFsharp','Microsoft.Extensions.DependencyInjection.Abstractions','Microsoft.Extensions.Logging.Abstractions','System.Security.Cryptography.Pkcs')
                    assert (value.get('resolved'),value.get('contentHash')) == (parent_io[name].get('resolved'),parent_io[name].get('contentHash'))
                    added.append({'lock': row['path'], 'package': name, 'resolved': value['resolved'], 'contentHash': value['contentHash']})
        old_non_sdk = {k for k,v in old[framework].items() if v.get('type') != 'Project' and k != 'OcctSharp' and not k.startswith('OcctSharp.')}
        assert old_non_sdk <= dependencies.keys()
    if count:
        locks.append({'path':row['path'],'sdkReferences':count,'sha256':sha(current)})
assert len(locks) == 30 and len(added) == 8
for fix in prep['configurationFixes']:
    assert sha(workspace / fix['path']) == fix['sha256']
references = []
for project in prep['projects']:
    tree = ET.parse(workspace / project)
    for item in tree.iter('PackageReference'):
        name = item.attrib.get('Include', '')
        if name == 'OcctSharp' or name.startswith('OcctSharp.'):
            assert name in packages and item.attrib['Version'] == '[$(OcctSharpVersion)]', project
            references.append({'project':project,'package':name})
assert len(references) == 50
payloads = []
for config in ('Release','Debug'):
    for project,suffix in (('Cadoryx.wpf','net10.0-windows/win-x64'),('Cadoryx.Tests','net10.0'),('Cadoryx.Cli','net10.0')):
        directory = workspace / project / 'bin' / config / suffix
        for name,expected in managed.items():
            assert sha(directory / name) == expected
        assert {p.name for p in (directory / 'occt').glob('*.dll')} == native.keys()
        for name,expected in native.items():
            assert sha(directory / 'occt' / name) == expected
        payloads.append({'configuration':config,'project':project,'directory':str(directory),'sdk13MatchesPublicPackage':True,'nativeDlls':len(native),'nativeMatchesPublisher':True})
usage = read(output / 'compiled-occt-usage.json')
assert not usage['directNativeImports']
if not args.current_production:
    original_usage = read(root / 'capability-evidence/diagnostics/public-nuget-migration/source1073-occt-usage.json')
    assert usage['typeCount'] == 221 and usage['memberCount'] == 696
    assert {r['symbol'] for r in usage['types']} == {r['symbol'] for r in original_usage['types']}
    assert {r['symbol'] for r in usage['members']} == {r['symbol'] for r in original_usage['members']}
for row in usage['assemblies']:
    assert sha(Path(row['path'])) == row['sha256']
report = output / 'verification.json'
assert not report.exists()
report.write_text(json.dumps({
    'recordedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'state':('PASS_PUBLIC_CURRENT_PRODUCTION_AND_TOOLS_50_PROJECTS_TWO_BUILDS_30_LOCKS_EXACT_PUBLIC_PAYLOAD_RUNTIME_NOT_RUN' if args.current_production else 'PASS_PUBLIC_FINAL1073_AND_TOOLS_50_PROJECTS_TWO_BUILDS_30_LOCKS_EXACT_PUBLIC_PAYLOAD_AND221_696_RUNTIME_NOT_RUN'),
    'version':public['version'],'preparation':ref(output / 'preparation.json'),'terminal':ref(output / 'exit.json'),
    'publicRestore':ref(output / 'public-restore-verification.json'),'inputFiles':len(prep['originalInputFiles']),
    'supportInputs':ref(output / 'support-inputs.json'),
    'source1073Retained':not args.current_production,'applicationTestAndProbeCodeOrAssertionsChanged':False,'changedFiles':changed,
    'projects':50,'stages':120,'sdkVersion':terminal['sdkVersion'],'resolvedSources':resolved_sources,'sdkLocks':locks,'directSdkReferences':references,'packages':packages,
    'existingNonSdkVersionsOrHashesChanged':False,'parentIoClosureAddedToTwoStaleToolLocks':added,
    'payloads':payloads,'compiledUsage':ref(output / 'compiled-occt-usage.json'),'typeCount':usage['typeCount'],'memberCount':usage['memberCount'],
    'runtimeExecuted':False,'productionApplied':False,'cleanup':False,'AF':'ACTIVE','G':'USER_DEFERRED'
},indent=2)+'\n',encoding='utf-8')
print(f'PASS public-only consumer:50 projects/both builds/30 SDK locks/exact public DLL payload/{usage["typeCount"]} types{usage["memberCount"]} members;actual runtime and production application NOT RUN.')
