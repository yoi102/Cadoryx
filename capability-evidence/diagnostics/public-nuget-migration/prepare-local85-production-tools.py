from pathlib import Path
import datetime, hashlib, json, re, shutil, subprocess

root = Path(__file__).resolve().parents[1]
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
output = root / 'artifacts/local85-production-tools-1'
assert not output.exists()
workspace = output / 'workspace'
workspace.mkdir(parents=True)
patterns = ['Cadoryx.*', 'Cadoryx.*/**', 'tools/**', 'integrations/occtsharp-boolean-history/Consumer/**', 'licenses/**', 'Directory.Build.props', 'NuGet.Config', 'LICENSE']
paths = sorted(set(subprocess.check_output(['git','ls-files','--cached','--others','--exclude-standard','--',*patterns], cwd=root, text=True).splitlines()))
originals = []
for relative in paths:
    path = Path(relative)
    assert not path.is_absolute() and '..' not in path.parts
    origin, target = root / path, workspace / path
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(origin, target)
    originals.append({'path': relative, 'sha256': sha(target)})
version = '8.0.1-preview.85'
feed = root / 'artifacts/public-occt-preview85-consumer1073-1/frozen-local-candidate-feed'
original_version = '8.0.1-preview.31.cadoryx.curve-inspection.26'
props = workspace / 'Directory.Build.props'
assert props.read_bytes().count(original_version.encode()) == 1
props.write_bytes(props.read_bytes().replace(original_version.encode(), version.encode()))
changed_projects = []
for path in workspace.rglob('*.csproj'):
    data = path.read_bytes()
    changed = re.sub(rb'(<PackageReference\s+Include="OcctSharp(?:\.[A-Za-z0-9.-]+)?"\s+Version=")[^"]+("[^>]*>)', rb'\g<1>[$(OcctSharpVersion)]\g<2>', data)
    changed = re.sub(rb'^[ \t]*<MulticopySdkVersion>[^<]+</MulticopySdkVersion>\r?\n', b'', changed, flags=re.M)
    if data != changed:
        path.write_bytes(changed)
        changed_projects.append(path.relative_to(workspace).as_posix())
assert len(changed_projects) == 6
(workspace / 'NuGet.Config').write_text(f'''<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear/><add key="frozen-local-candidate" value="{feed.as_posix()}"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><packageSource key="frozen-local-candidate"><package pattern="OcctSharp"/><package pattern="OcctSharp.*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping></configuration>
''', encoding='utf-8')
manifest = read(root / 'artifacts/public-occt-preview85-consumer1073-1/publisher-runtime-manifest.json')
baseline_path = workspace / 'Cadoryx.wpf/runtime-baseline.json'
baseline = read(baseline_path)
bridge = next(row['sha256'] for row in manifest['files'] if row['path'] == 'occt/OcctSharp.Native.dll')
baseline.update(occtSharpPackage=version,nativeAbi=manifest['nativeAbi'],bridgeVersion=manifest['bridgeVersion'],nativeSha256=bridge.lower())
baseline_path.write_text(json.dumps(baseline,indent=2)+'\n',encoding='utf-8')
projects = sorted(path.relative_to(workspace).as_posix() for path in workspace.rglob('*.csproj'))
assert len(projects) == 50
(output / 'preparation.json').write_text(json.dumps({
    'recordedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'state':'ISOLATED_PRODUCTION_AND_TOOLS_SHARED_LOCAL85_DEPENDENCY_PREPARATION_NOT_PUBLIC_OR_RUNTIME',
    'workspace':str(workspace),'version':version,'frozenLocalFeed':str(feed),
    'originalInputFiles':originals,'changedProjectVersionExpressions':changed_projects,
    'projects':projects,'applicationOrTestCodeChanged':False,
    'runtimeExecuted':False,'productionApplied':False,'publicationConfirmed':False,'AF':'ACTIVE','G':'USER_DEFERRED'
},indent=2)+'\n',encoding='utf-8')
print('PASS isolated production/tools inputs and six shared SDK reference changes;50 projects;actual restore/build NOT RUN.')
