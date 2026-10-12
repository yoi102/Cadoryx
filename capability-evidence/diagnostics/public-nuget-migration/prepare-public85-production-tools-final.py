from pathlib import Path
import datetime, hashlib, json, shutil, xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
old = read(root / 'artifacts/local85-production-tools-1/preparation.json')
proof = root / 'artifacts/local85-production-tools-1/verification.json'
assert read(proof)['totalProjects'] == 50 and len(old['originalInputFiles']) == 1142
output = root / 'artifacts/public85-production-tools-final-1'
assert not output.exists()
frozen, workspace = output / 'original-inputs', output / 'workspace'
frozen.mkdir(parents=True)
workspace.mkdir()
originals = []
for row in old['originalInputFiles']:
    relative = Path(row['path'])
    assert not relative.is_absolute() and '..' not in relative.parts
    origin = root / relative
    assert sha(origin) == row['sha256'], row['path']
    for directory in (frozen,workspace):
        target = directory / relative
        target.parent.mkdir(parents=True,exist_ok=True)
        shutil.copy2(origin,target)
        assert sha(target) == row['sha256']
    originals.append({**row,'origin':str(frozen / relative),'group':'current-production-and-tools'})
version = '8.0.1-preview.85'
props = workspace / 'Directory.Build.props'
prior = ET.fromstring(props.read_bytes()).findtext('.//OcctSharpVersion')
assert prior == '8.0.1-preview.31.cadoryx.curve-inspection.26'
assert props.read_bytes().count(prior.encode()) == 1
props.write_bytes(props.read_bytes().replace(prior.encode(),version.encode()))
shutil.copy2(root / 'artifacts/public85-final1073-1/workspace/NuGet.Config',workspace / 'NuGet.Config')
configs = {*old['changedProjectVersionExpressions'],'tools/AnalyticalOffsetProbe/AnalyticalOffsetProbe.csproj'}
fixes = []
for relative in sorted(configs):
    origin, target = Path(old['workspace']) / relative, workspace / relative
    shutil.copy2(origin,target)
    fixes.append({'path':relative,'sha256':sha(target),'origin':{'path':str(origin),'sha256':sha(origin)}})
changed = [r['path'] for r in originals if sha(workspace / r['path']) != r['sha256']]
assert set(changed) == {'Directory.Build.props','NuGet.Config',*configs}
projects = sorted(p.relative_to(workspace).as_posix() for p in workspace.rglob('*.csproj'))
assert projects == old['projects'] and len(projects) == 50
(output / 'preparation.json').write_text(json.dumps({
    'recordedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'state':'PREPARED_CURRENT_PRODUCTION_AND_TOOLS_PUBLIC_RESTORE_BUILD_RUNTIME_NOT_RUN',
    'version':version,'workspace':str(workspace),'originalInputFiles':originals,
    'configurationFixes':fixes,'changedFiles':sorted(changed),'projects':projects,
    'preparedInputFiles':[{'path':r['path'],'sha256':sha(workspace / r['path'])} for r in originals],
    'priorLocalBuild':{'path':str(proof),'sha256':sha(proof)},
    'applicationTestAndProbeCodeOrAssertionsChanged':False,
    'productionApplied':False,'runtimeExecuted':False,'publicRestorePassed':False,
    'cleanup':False,'AF':'ACTIVE','G':'USER_DEFERRED'
},indent=2)+'\n',encoding='utf-8')
relative = 'scripts/nuget-package-cache.ps1'
origin, target = root / relative, workspace / relative
target.parent.mkdir(parents=True)
shutil.copy2(origin,target)
(output / 'support-inputs.json').write_text(json.dumps({
    'state':'PREPARED_EXACT_TOOL_SCRIPT_DEPENDENCY_NOT_RUNTIME_ACCEPTANCE',
    'files':[{'path':relative,'origin':str(origin),'sha256':sha(target)}]
},indent=2)+'\n',encoding='utf-8')
print('PASS current production and tools:1142 immutable inputs+helper/50 projects;9 configuration changes. Public restore/build/runtime NOT RUN.')
