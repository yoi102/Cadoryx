from pathlib import Path
import datetime, hashlib, json, shutil

root = Path(__file__).resolve().parents[1]
output = root / 'artifacts/public85-production-tools-final-1'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
proof, prep = read(output / 'verification.json'), read(output / 'preparation.json')
assert proof['state'] == 'PASS_PUBLIC_CURRENT_PRODUCTION_AND_TOOLS_50_PROJECTS_TWO_BUILDS_30_LOCKS_EXACT_PUBLIC_PAYLOAD_RUNTIME_NOT_RUN'
assert proof['sdkVersion'] == '10.0.401' and proof['projects'] == 50
workspace = Path(prep['workspace'])
originals = {r['path']:r for r in prep['originalInputFiles']}
for relative, row in originals.items():
    assert sha(root / relative) == row['sha256'], ('Working source changed; preserve it',relative)
required = {*prep['changedFiles'],'Cadoryx.wpf/runtime-baseline.json',*(r['path'] for r in proof['sdkLocks'])}
allowed = set(proof['changedFiles'])
assert required <= allowed
assert allowed - required == {'tools/SketchProbe/packages.lock.json'}
# SketchProbe has no SDK reference, but its existing IO project reference
# requires the same PDFsharp dependency closure already audited for StorageBench.
parent_io = read(workspace / 'Cadoryx.IO/packages.lock.json')['dependencies']['net10.0']
for relative in allowed:
    assert relative in required or relative.endswith('/packages.lock.json')
    assert sha(root / relative) == originals[relative]['sha256']
    if not relative.endswith('/packages.lock.json'):
        continue
    old = read(root / relative)['dependencies']
    new = read(workspace / relative)['dependencies']
    assert old.keys() == new.keys()
    for framework, dependencies in new.items():
        before = old[framework]
        for name, value in dependencies.items():
            if name == 'OcctSharp' or name.startswith('OcctSharp.') or value.get('type') == 'Project':
                continue
            if name in before:
                assert value == before[name], (relative, name)
            else:
                assert relative in ('tools/SketchProbe/packages.lock.json','tools/StorageBench/packages.lock.json')
                assert name in ('PDFsharp','Microsoft.Extensions.DependencyInjection.Abstractions','Microsoft.Extensions.Logging.Abstractions','System.Security.Cryptography.Pkcs')
                parent = parent_io[name]
                expected = {k:v for k,v in parent.items() if k not in ('type','requested')}
                assert value == {'type':'Transitive', **expected}, (relative, name)
        assert {k for k,v in before.items() if v.get('type') != 'Project' and k != 'OcctSharp' and not k.startswith('OcctSharp.')} <= dependencies.keys()
record = root / 'artifacts/public85-production-applied-1.json'
assert not record.exists()
rows = []
for relative in sorted(allowed):
    target, origin = root / relative, workspace / relative
    assert sha(target) == originals[relative]['sha256']
    shutil.copy2(origin,target)
    rows.append({'path':relative,'beforeSha256':originals[relative]['sha256'],'afterSha256':sha(target)})
# The SDK otherwise implicitly adds its machine-local library-packs source.
# This consumer restores its NuGet dependencies only from the public feed.
props = root / 'Directory.Build.props'
data = props.read_bytes()
assert b'<DisableImplicitLibraryPacksFolder>' not in data
data = data.replace(b'    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>',b'    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>\r\n    <DisableImplicitLibraryPacksFolder>true</DisableImplicitLibraryPacksFolder>')
props.write_bytes(data)
next(r for r in rows if r['path']=='Directory.Build.props')['afterSha256']=sha(props)
assert read(root / 'global.json')['sdk']['version'] == '10.0.401'
record.write_text(json.dumps({
    'recordedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'state':'PUBLIC85_CONFIGURATION_AND30_SDK_LOCKS_AND_STALE_TOOL_CLOSURE_APPLIED_FROM_AUDITED_CURRENT_PRODUCTION_CLONE_ROOT_RUNTIME_NOT_RUN',
    'consumerProof':{'path':str(output / 'verification.json'),'sha256':sha(output / 'verification.json')},
    'files':rows,'applicationTestOrProbeCodeChanged':False,'AFSource1073Merged':False,
    'rootBuildPassed':False,'runtimePassed':False,'CadoryxRequiresVcpkg':False,
    'cleanup':False,'AF':'ACTIVE','G':'USER_DEFERRED'
},indent=2)+'\n',encoding='utf-8')
print(f'PASS production dependency configuration: {len(rows)} files/30 SDK locks;code unchanged;root build/runtime NOT RUN.')
