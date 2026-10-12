from pathlib import Path
import datetime,hashlib,json
root=Path('C:/Users/yoiri/source/repos/Cadoryx')
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref=lambda p:{'path':str(p),'sha256':sha(p)}
task=root/'artifacts/d6-sdk30-window-host-1'
assert not task.exists()
build_path=root/'artifacts/d6-sdk30-production-integration-1/build-both-1-verification.json'
build=read(build_path);assert build['sourceCount']==1066 and build['inventoryCount']==6530
prior=root/'capability-evidence/AF_PENDING_D6_WINDOW_OBSERVER_V3_BUILD_VERIFICATION.json'
prior_data=read(prior);assert prior_data['sourceCount']==1073 and len(prior_data['uiMatrix'])==36
for row in prior_data['source']:assert sha(Path(row['path']))==row['sha256']
task.mkdir()
canonical=root/'capability-evidence/diagnostics/d6-window-acceptance'
for name in ('WindowHost.cs','WindowHost.csproj'):
    (task/name).write_bytes((canonical/name).read_bytes())
    assert sha(task/name)==sha(root/'artifacts/d6-window-host-5'/name)
text=(canonical/'build-audit.py').read_text(encoding='utf-8-sig')
for before,after in (
    ('artifacts/d6-window-host-5','artifacts/d6-sdk30-window-host-1'),
    ('artifacts/d6-finite-newbody-draft-4/build-both-1-verification.json','artifacts/d6-sdk30-production-integration-1/build-both-1-verification.json'),
    ('==1073','==1066'),('SOURCE1073','SOURCE1066_SDK30'),("'sourceCount':1073","'sourceCount':1066"),
    ('AF_PENDING_D6_WINDOW_OBSERVER_V3_BUILD_VERIFICATION.json','AF_PENDING_D6_SDK30_WINDOW_OBSERVER_BUILD_VERIFICATION.json'),
    ('full6619TerminalRequiredBeforeWindowStart','full6530BothPassRequiredBeforeWindowStart'),
):
    assert before in text,before
    text=text.replace(before,after)
text=text.replace(" 'candidateBuild':ref(build_path)"," 'sdkVersion':build['sdkVersion'],'fixturesGeneratedUnderSdk31ActualSdk30LoadAndPreviewRequired':True,\n 'candidateBuild':ref(build_path)")
text=text.replace("configs={}\n", "configs={}\nfor config in ('Release','Debug'):\n full_path=root/'artifacts/d6-sdk30-production-integration-1'/('full6530-'+config.lower()+'-1-verification.json')\n full=read(full_path);assert full['passed']==6530 and full['failed']==full['skipped']==0\n assert full['build']==ref(build_path)\n")
(task/'build-audit.py').write_text(text,encoding='utf-8')
runner=root/'capability-evidence/diagnostics/d6-sdk30-integration/build-window-after-queue.ps1'
prep={'recordedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'state':'SDK30_SOURCE1066_WINDOW_OBSERVER_SOURCE_FROZEN_NOT_COMPILED_NOT_RUN',
    'candidateBuild':ref(build_path),'sourceCount':1066,'sdkVersion':build['sdkVersion'],
    'source':[ref(task/f) for f in ('WindowHost.cs','WindowHost.csproj','build-audit.py')],
    'priorSdk31ObserverBuild':ref(prior),'sameObserverCsAndProjectBytes':True,
    'runner':ref(runner),'matrixCount':36,'readinessSeconds':30,'perWindowSeconds':300,
    'allTenOriginalRuntimeQueueTerminalsRequiredBeforeCompile':True,
    'sdk30FullBothPassRequiredBeforeCompile':True,'fixturesGeneratedUnderSdk31ActualSdk30LoadAndPreviewRequired':True,
    'compilationStarted':False,'actualWindowsStarted':False,'actualWindowsPassed':False,
    'productionApplied':False,'cleanup':False,'AF':'ACTIVE','G':'USER_DEFERRED'}
(task/'preparation-1.json').write_text(json.dumps(prep,indent=2)+'\n',encoding='utf-8')
out=root/'capability-evidence/AF_PENDING_D6_SDK30_WINDOW_OBSERVER_PREPARATION.json';assert not out.exists()
out.write_text(json.dumps(prep,indent=2)+'\n',encoding='utf-8')
print('PASS prepared SDK30 source1066 observer with unchanged Cs/project,36 routes and30/300s. Compile/runtime remain NOT RUN.')
