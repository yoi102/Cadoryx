"""Freeze current application sources in a new final87-only workspace."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib, json, shutil, subprocess, xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
STAGE = ROOT / 'artifacts/af-final87-1'; WORK = STAGE / 'workspace'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
sdk = read(STAGE / 'sdk-reception.json')
assert sdk['version'] == '8.0.1-preview.87' and not sdk['applicationAccepted']
for row in sdk['packages']: assert sha(Path(sdk['feed']) / row['name']) == row['sha256']
assert not WORK.exists(); WORK.mkdir()
tracked = subprocess.check_output(['git','ls-files'],cwd=ROOT,text=True).splitlines()
paths = [p for p in tracked if not p.startswith(('docs/','capability-evidence/')) and (not p.endswith('.md') or p.startswith('licenses/'))]
for p in ('Directory.Build.props','NuGet.Config','global.json','Cadoryx.slnx'): assert p in paths
source = []
for name in paths:
    original = ROOT / name; target = WORK / name
    target.parent.mkdir(parents=True,exist_ok=True); shutil.copyfile(original,target)
    source.append(dict(path=name,sha256=sha(original)))
props = WORK / 'Directory.Build.props'
text = props.read_text(encoding='utf-8-sig'); assert text.count('8.0.1-preview.87.cu1') == 1
props.write_text(text.replace('8.0.1-preview.87.cu1',sdk['version']),encoding='utf-8')
(WORK / 'NuGet.Config').write_text(f'''<?xml version="1.0" encoding="utf-8"?>
<configuration>
 <packageSources><clear/><add key="final87" value="{sdk['feed']}"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
 <packageSourceMapping><packageSource key="final87"><package pattern="OcctSharp*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>
''',encoding='utf-8')
baseline = read(WORK / 'Cadoryx.wpf/runtime-baseline.json')
baseline.update(occtSharpPackage=sdk['version'],nativeAbi=sdk['nativeAbi'],bridgeVersion=sdk['bridgeVersion'],
    nativeSha256=next(r['sha256'].lower() for r in sdk['native62'] if r['name']=='OcctSharp.Native.dll'))
(WORK / 'Cadoryx.wpf/runtime-baseline.json').write_text(json.dumps(baseline,indent=2)+'\n',encoding='utf-8')
previous = read(ROOT / 'artifacts/af1082-f5-d7-local87cu1-1/preparation.json')
projects = previous['projects'] + ['tools/LmStudioTransportTests/LmStudioTransportTests.csproj',
    'tools/AgentSelectionContextTests/AgentSelectionContextTests.csproj',
    'tools/CadAgentWorkflowProbeRawTransport/CadAgentWorkflowProbeRawTransport.csproj']
assert len(projects) == len(set(projects)) == 53 and all((WORK / p).exists() for p in projects)
for name in projects:
    for ref in ET.parse(WORK / name).findall('.//ProjectReference'):
        assert 'OcctSharp' not in ref.attrib['Include'], name
(WORK / 'CandidateAllProjects.slnx').write_text('<Solution>\n'+''.join(f' <Project Path="{p}"/>\n' for p in projects)+'</Solution>\n',encoding='utf-8')
projection = read(ROOT / 'artifacts/f5-lmstudio-completion-integrity-1/raw-source1/projection-verification.json')
for row in projection['originals']+[projection['projected']]: assert sha(ROOT / row['path']) == row['sha256']
raw = WORK / 'tools/CadAgentWorkflowProbeRawTransport/RecordedProgram.cs'
shutil.copyfile(ROOT / projection['projected']['path'],raw)
mutations = ['Directory.Build.props','NuGet.Config','Cadoryx.wpf/runtime-baseline.json']
record = dict(recordedAt=datetime.now(timezone.utc).isoformat(),state='CURRENT_APPLICATION_FINAL87_ISOLATED_SOURCE_FROZEN_NOT_BUILT',
    baseHead=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip(), workspace=str(WORK),
    sdkReception=dict(path=str(STAGE/'sdk-reception.json'),sha256=sha(STAGE/'sdk-reception.json')),
    rootSource=source, candidateChanges=mutations, projects=projects, rawProgramSource=str(raw),
    rawProjection=projection, expectedTotal=7192, deferredCount=24, expectedExecuted=7168,
    originalTestsModified=False, originalPromptsAssertionsAndBudgetsModified=False,
    productionApplied=False, publicNuget=False, AFComplete=False)
(STAGE/'preparation.json').write_text(json.dumps(record,indent=2)+'\n',encoding='utf-8')
print(f'Frozen {len(source)} tracked sources and53 projects; exact final87 only; current7192 pending discovery, exact24 deferred.')
