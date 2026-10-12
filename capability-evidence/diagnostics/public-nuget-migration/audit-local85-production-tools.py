from pathlib import Path
import datetime, hashlib, json

root = Path(__file__).resolve().parents[1]
output = root / 'artifacts/local85-production-tools-1'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref = lambda p: {'path':str(p),'sha256':sha(p)}
prep = read(output / 'preparation.json')
workspace = Path(prep['workspace'])
sdk = read(root / 'artifacts/public-occt-preview85-consumer1073-1/verification.json')
stages = []
failed = read(output / 'failure.json')['completedStages']
stages.extend(row for row in failed if row['exitCode'] == 0 and row['target'] in ('Cadoryx.slnx','integrations/occtsharp-boolean-history/Consumer/Consumer.csproj','tools/AgentConnectionProbe/AgentConnectionProbe.csproj'))
for stage in ('restore','restore-locked','release','debug'):
    stages.append(read(output / f'AnalyticalOffsetNativeProbe-{stage}-3-exit.json'))
stages.extend(read(output / 'exit-4.json')['stages'])
assert len(stages) == 120 and all(row['exitCode'] == 0 for row in stages)
assert len({(row['target'],row['stage']) for row in stages}) == 120
assert len({row['target'] for row in stages}) == 30
for row in stages:
    log = Path(row['log'])
    if row['stage'] in ('Release','Debug'):
        text = log.read_text(encoding='utf-8-sig')
        assert '0 Warning(s)' in text and '0 Error(s)' in text, row
allowed = {'Directory.Build.props','NuGet.Config','Cadoryx.wpf/runtime-baseline.json',*prep['changedProjectVersionExpressions'],'tools/AnalyticalOffsetProbe/AnalyticalOffsetProbe.csproj'}
changed = []
locks = []
new_tool_transitives = []
parent_io = read(root / 'Cadoryx.IO/packages.lock.json')['dependencies']['net10.0']
for row in prep['originalInputFiles']:
    current = workspace / row['path']
    if sha(current) != row['sha256']:
        assert row['path'] in allowed or row['path'].endswith('/packages.lock.json'), row['path']
        changed.append(row['path'])
    if row['path'].endswith('/packages.lock.json'):
        original, updated = read(root / row['path']), read(current)
        assert sha(root / row['path']) == row['sha256']
        assert original['dependencies'].keys() == updated['dependencies'].keys()
        count = 0
        for framework, dependencies in updated['dependencies'].items():
            old = original['dependencies'][framework]
            for name, value in dependencies.items():
                if name == 'OcctSharp' or name.startswith('OcctSharp.'):
                    assert value['resolved'] == prep['version'] and value['contentHash'] == sdk['packages'][name]['contentHash'], (row['path'],name)
                    count += 1
                elif value.get('type') != 'Project':
                    if name in old:
                        assert (value.get('resolved'),value.get('contentHash')) == (old[name].get('resolved'),old[name].get('contentHash')), (row['path'],name)
                    else:
                        assert row['path'] in ('tools/SketchProbe/packages.lock.json','tools/StorageBench/packages.lock.json')
                        assert name in ('PDFsharp','Microsoft.Extensions.DependencyInjection.Abstractions','Microsoft.Extensions.Logging.Abstractions','System.Security.Cryptography.Pkcs')
                        assert (value.get('resolved'),value.get('contentHash')) == (parent_io[name].get('resolved'),parent_io[name].get('contentHash'))
                        new_tool_transitives.append({'lock':row['path'],'package':name,'resolved':value['resolved'],'contentHash':value['contentHash'],'reason':'Existing Cadoryx.IO PDFsharp closure missing from stale tool lock; no existing version changed'})
            assert {k for k,v in old.items() if v.get('type') != 'Project' and k != 'OcctSharp' and not k.startswith('OcctSharp.')} <= {k for k,v in dependencies.items() if v.get('type') != 'Project' and k != 'OcctSharp' and not k.startswith('OcctSharp.')}
        if count:
            locks.append({'path':row['path'],'sha256':sha(current),'sdkReferences':count})
assert len(locks) == 30
assert len(new_tool_transitives) == 8
projects = set(prep['projects'])
solution = __import__('xml.etree.ElementTree',fromlist=['']).parse(workspace / 'Cadoryx.slnx')
covered = {item.attrib['Path'] for item in solution.iter('Project')} | {row['target'] for row in stages if row['target'] != 'Cadoryx.slnx'}
assert covered == projects and len(projects) == 50
report_path = output / 'verification.json'
assert not report_path.exists()
report_path.write_text(json.dumps({
    'recordedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'state':'PASS_ISOLATED_PRODUCTION_AND29_CONSUMERS_50_PROJECTS_TWO_BUILDS_LOCAL85_SHARED_VERSION30_LOCKS_NO_RUNTIME_OR_PUBLIC_ACCEPTANCE',
    'version':prep['version'],'preparation':ref(output / 'preparation.json'),
    'localSdkProof':ref(root / 'artifacts/public-occt-preview85-consumer1073-1/verification.json'),
    'totalProjects':50,'standaloneConsumers':29,'stages':stages,
    'changedConfigurationFiles':changed,'applicationTestAndProbeCodeOrAssertionsChanged':False,
    'sdkConsumerLocks':locks,'existingNonSdkPackageVersionsAndHashesChanged':False,'existingParentIoClosureAddedToTwoStaleToolLocks':new_tool_transitives,
    'configurationFixes':['Six old explicit SDK versions now use shared OcctSharpVersion','ThreadDrawingProbe uses explicit FrozenCadoryx instead of an old build directory','AnalyticalOffsetProbe uses explicit frozen payload and references required Kernel.Abstractions'],
    'priorFailedAttemptsRetained':[ref(output / 'failure.json'),ref(output / 'failure-2.json')],
    'runtimeExecuted':False,'publicationConfirmed':False,'publicSourceOnly':False,
    'productionApplied':False,'cleanup':False,'AF':'ACTIVE','G':'USER_DEFERRED'
},indent=2)+'\n',encoding='utf-8')
print('PASS50 projects/29 standalone consumers: ordinary+locked and both zero-warning builds;30 SDK locks;only configuration changed. Local candidate only;runtime/remote/production NOT RUN.')
