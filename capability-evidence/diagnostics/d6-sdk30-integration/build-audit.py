from pathlib import Path
import datetime,hashlib,json
root=Path('C:/Users/yoiri/source/repos/Cadoryx')
task=root/'artifacts/d6-sdk30-production-integration-1'
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref=lambda p:{'path':str(p),'sha256':sha(p)}
prep=read(task/'preparation-1.json');workspace=Path(prep['workspace'])
baseline=read(Path(prep['baselineBuild']['path']))
assert len(prep['sourcePayload'])==1066
for row in prep['sourcePayload']:assert sha(workspace/row['path'])==row['sha256'],row['path']
for field in ('baselineBuild','baselinePreparation','originalD6Preparation','originalD6Functional'):
    row=prep[field];assert sha(Path(row['path']))==row['sha256']
for row in prep['baselineFull6467Both']:
    assert sha(Path(row['path']))==row['sha256']
    data=read(Path(row['path']));assert data['passed']==6467 and data['failed']==0 and data['skipped']==0
terminal=read(task/'build-both-1-exit.json');assert len(terminal['gates'])==5
for gate in terminal['gates']:
    assert gate['exitCode']==0 and sha(Path(gate['terminal']))==gate['sha256']
sdk_version='8.0.1-preview.31.cadoryx.generated-defeature-history.30'
assert sdk_version in (workspace/'Directory.Build.props').read_text(encoding='utf-8-sig')
assert '.cadoryx.exact-step.31' not in (workspace/'Directory.Build.props').read_text(encoding='utf-8-sig')
configs={}
for config in ('Release','Debug'):
    label=config.lower()
    log=task/f'build-{label}-1.log';text=log.read_text(encoding='utf-8-sig')
    assert '0 Warning(s)' in text and '0 Error(s)' in text
    for step in ('build-'+label,'inventory-'+label):assert (task/f'{step}-1-stderr.log').stat().st_size==0
    inventory=task/f'inventory-{label}-1.log'
    names=[s.strip() for s in inventory.read_text(encoding='utf-8-sig').splitlines() if s.strip().startswith('Cadoryx.Tests.')]
    original=baseline['configurations'][config]['inventoryNames']
    assert len(original)==6467 and set(original).issubset(names)
    assert len(names)==len(set(names))==6530
    new=set(names)-set(original)
    assert len(new)==63 and all('.FiniteSurfaceNewBodyTests.' in n for n in new)
    finite=[n for n in names if '.FiniteSurfaceExtrusionTests.' in n or '.FiniteSurfaceNewBodyTests.' in n]
    classes=('AgentIdentityFieldPresentationTests','ToolPresentationTests','AgentSuccessfulActionReceiptTests','AgentEmptyResponseRecoveryTests','DrawingInspectionTypeFeedbackTests','DrawingContractRequestGuideTests','DrawingAgentTests')
    agent=[n for n in names if any('.'+c+'.' in n.split('(')[0] for c in classes)]
    assert len(finite)==99 and len(agent)==165
    test=workspace/f'Cadoryx.Tests/bin/{config}/net10.0'
    app=workspace/f'Cadoryx.wpf/bin/{config}/net10.0-windows/win-x64'
    for directory in (test,app):
        for row in prep['native62']:assert sha(directory/'occt'/row['name'])==row['sha256']
        for row in prep['sdk13']:assert sha(directory/row['name'])==row['sha256']
    for lock in workspace.glob('*/packages.lock.json'):
        data=read(lock)
        for dependencies in data['dependencies'].values():
            for name,details in dependencies.items():
                if name.startswith('OcctSharp'):assert details['resolved']==sdk_version,(lock,name)
    payload=lambda directory:[{'path':p.relative_to(directory).as_posix(),'sha256':sha(p)} for p in sorted(directory.rglob('*')) if p.is_file()]
    configs[config]={'testDirectory':str(test),'testPayload':payload(test),
        'appDirectory':str(app),'appPayload':payload(app),'inventoryNames':names,'newNames':sorted(new),
        'relatedNames':finite,'agentNames':agent,'buildLog':ref(log),'inventoryLog':ref(inventory)}
out=task/'build-both-1-verification.json';assert not out.exists()
out.write_text(json.dumps({'recordedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'state':'PASS_D6_SOURCE1066_SDK30_BUILD_BOTH_ORIGINAL6467_AND_NEW63_NAMES_UNCHANGED',
    'workspace':str(workspace),'sourcePayload':prep['sourcePayload'],'configurations':configs,
    'native62':prep['native62'],'sdk13':prep['sdk13'],'sdkVersion':sdk_version,
    'preparation':ref(task/'preparation-1.json'),'buildTerminal':ref(task/'build-both-1-exit.json'),
    'sourceCount':1066,'inventoryCount':6530,'original6467NamesPreserved':True,
    'nativeBindingsAdded':False,'sdk31CandidateChanged':False,'actualRuntimeTestsStarted':False,
    'actualWindowsStarted':False,'productionApplied':False,'cleanup':False,'AF':'ACTIVE','G':'USER_DEFERRED'},indent=2)+'\n',encoding='utf-8')
print('PASS source1066 SDK30 build both0 warnings/errors; original6467+63=6530; finite99/agent165 names; SDK13/native62 disk payload.')
