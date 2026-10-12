from pathlib import Path
import datetime, hashlib, json, shutil, xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref = lambda p: {'path': str(p), 'sha256': sha(p)}
source_proof = root / 'artifacts/d6-finite-newbody-draft-4/build-both-1-verification.json'
tool_proof = root / 'artifacts/local85-production-tools-1/verification.json'
source, tools = read(source_proof), read(tool_proof)
tool_preparation = Path(tools['preparation']['path'])
assert sha(tool_preparation) == tools['preparation']['sha256']
tool_prep = read(tool_preparation)
assert len(source['sourcePayload']) == 1073
assert tools['totalProjects'] == 50 and len(tools['stages']) == 120
assert all(row['exitCode'] == 0 for row in tools['stages'])
output = root / 'artifacts/public85-final1073-1'
assert not output.exists(), 'Fresh immutable preparation required'
workspace = output / 'workspace'
workspace.mkdir(parents=True)
originals = []
for row in source['sourcePayload']:
    relative = Path(row['path'])
    assert not relative.is_absolute() and '..' not in relative.parts
    origin, target = Path(source['workspace']) / relative, workspace / relative
    assert sha(origin) == row['sha256'], row['path']
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(origin, target)
    originals.append({**row, 'origin': str(origin), 'group': 'source1073'})
for row in tool_prep['originalInputFiles']:
    if not row['path'].startswith(('tools/', 'integrations/')):
        continue
    relative = Path(row['path'])
    assert not relative.is_absolute() and '..' not in relative.parts
    origin, target = root / relative, workspace / relative
    assert not target.exists() and sha(origin) == row['sha256'], row['path']
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(origin, target)
    originals.append({**row, 'origin': str(origin), 'group': 'current-tools'})

version = '8.0.1-preview.85'
props = workspace / 'Directory.Build.props'
old = ET.fromstring(props.read_bytes()).findtext('.//OcctSharpVersion')
assert old == '8.0.1-preview.31.cadoryx.exact-step.31'
assert props.read_bytes().count(old.encode()) == 1
props.write_bytes(props.read_bytes().replace(old.encode(), version.encode()))
(workspace / 'NuGet.Config').write_text('''<?xml version="1.0" encoding="utf-8"?>
<configuration>
 <packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
 <packageSourceMapping><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>
''', encoding='utf-8')
configs = {*tool_prep['changedProjectVersionExpressions'], 'tools/AnalyticalOffsetProbe/AnalyticalOffsetProbe.csproj'}
configuration_fixes = []
for relative in sorted(configs):
    origin = Path(tool_prep['workspace']) / relative
    target = workspace / relative
    assert origin.is_file() and target.is_file()
    shutil.copy2(origin, target)
    configuration_fixes.append({'path': relative, 'sha256': sha(target), 'origin': ref(origin)})

# The final publisher manifest and public package hashes are bound by the runner
# only after a fresh public-only restore passes. No local candidate hashes become
# public acceptance merely by copying this workspace.
projects = sorted(p.relative_to(workspace).as_posix() for p in workspace.rglob('*.csproj'))
assert len(projects) == 50
allowed = {'Directory.Build.props', 'NuGet.Config', *configs}
changed = []
for row in originals:
    if sha(workspace / row['path']) != row['sha256']:
        assert row['path'] in allowed, row['path']
        changed.append(row['path'])
assert set(changed) == allowed
(output / 'preparation.json').write_text(json.dumps({
    'recordedAt': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'state': 'PREPARED_PUBLIC_ONLY_FINAL1073_AND_TOOLS_PUBLIC_RESTORE_BUILD_RUNTIME_NOT_RUN',
    'version': version, 'workspace': str(workspace), 'sourceBuild': ref(source_proof),
    'toolBuild': ref(tool_proof), 'originalInputFiles': originals,
    'configurationFixes': configuration_fixes, 'changedFiles': sorted(changed),
    'projects': projects, 'preparedInputFiles': [
        {'path': r['path'], 'sha256': sha(workspace / r['path'])} for r in originals
    ], 'applicationTestAndProbeCodeOrAssertionsChanged': False,
    'publisherManifestBound': False, 'publicRestorePassed': False,
    'runtimeExecuted': False, 'productionApplied': False,
    'cleanup': False, 'AF': 'ACTIVE', 'G': 'USER_DEFERRED'
}, indent=2) + '\n', encoding='utf-8')
helper_relative = 'scripts/nuget-package-cache.ps1'
helper_origin, helper_target = root / helper_relative, workspace / helper_relative
assert not helper_target.exists()
helper_target.parent.mkdir(parents=True, exist_ok=True)
shutil.copy2(helper_origin, helper_target)
assert sha(helper_origin) == sha(helper_target)
(output / 'support-inputs.json').write_text(json.dumps({
    'state': 'PREPARED_EXACT_TOOL_SCRIPT_DEPENDENCY_NOT_RUNTIME_ACCEPTANCE',
    'files': [{'path': helper_relative, 'origin': str(helper_origin), 'sha256': sha(helper_target)}],
    'reason': 'OcctCapabilityProbe Verify.ps1 reads the restored NuGet cache through this repository helper'
}, indent=2) + '\n', encoding='utf-8')
print(f'PASS preparation: source1073 plus tools, {len(originals)} immutable inputs /50 projects; only nine configuration files changed. Public restore/build/runtime NOT RUN.')
