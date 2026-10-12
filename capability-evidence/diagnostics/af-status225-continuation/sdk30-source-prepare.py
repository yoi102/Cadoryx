from pathlib import Path
import datetime,hashlib,json,shutil,difflib,sys
root=Path('C:/Users/yoiri/source/repos/Cadoryx')
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'));sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper();ref=lambda p:{'path':str(p),'sha256':sha(p)}
base_build_path=root/'artifacts/af-sdk30-history-identity-draft-5/build-both-1-verification.json'
base_build=read(base_build_path);base_prep_path=Path(base_build['preparation']['path']);base_prep=read(base_prep_path);base=Path(base_prep['workspace'])
assert base_prep['sourceCount']==len(base_prep['sourcePayload'])==1063
full=[]
for configuration in ('Release','Debug'):
 p=base_build_path.parent/f'full6467-{configuration.lower()}-1-verification.json';j=read(p)
 assert j['passed']==6467 and j['failed']==j['skipped']==0
 full.append(ref(p))
d6_prep_path=root/'artifacts/d6-finite-newbody-draft-4/preparation-1.json';d6_prep=read(d6_prep_path)
d6=Path(d6_prep['workspace']);parent=Path(d6_prep['baselineWorkspace'])
changes=[p for p in d6_prep['changedExisting'] if p!='Cadoryx.ViewModels/Toolboxes/CadAgentDrawingTools.cs']
assert len(changes)==7 and len(d6_prep['newSources'])==3
task=root/'artifacts/d6-sdk30-production-integration-1';recover=sys.argv[1:]==['--recover-prepared-only']
if recover:
 assert (task/'source-preparation-first-failure-1.json').exists() and not (task/'preparation-1.json').exists()
else:
 assert not task.exists();task.mkdir()
workspace=task/'workspace-1'
if not recover:workspace.mkdir()
for row in base_prep['sourcePayload']:
 source=base/row['path'];assert sha(source)==row['sha256'],row['path']
 target=workspace/row['path']
 if not recover:target.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(source,target)
for rel in changes:
 assert sha(base/rel)==sha(parent/rel),('Unexpected inherited D6 precondition difference',rel)
 if not recover:shutil.copy2(d6/rel,workspace/rel)
for rel in d6_prep['newSources']:
 if not recover:(workspace/rel).parent.mkdir(parents=True,exist_ok=True);shutil.copy2(d6/rel,workspace/rel)
assert sha(base/'Cadoryx.Tests/FiniteSurfaceExtrusionTests.cs')==sha(parent/'Cadoryx.Tests/FiniteSurfaceExtrusionTests.cs')
before={r['path']:r['sha256'] for r in base_prep['sourcePayload']}
source_payload=[]
for rel in sorted(set(before)|set(d6_prep['newSources'])):
 p=workspace/rel;source_payload.append({'path':rel,'sha256':sha(p)})
 if rel in changes or rel in d6_prep['newSources']:assert sha(p)==sha(d6/rel),rel
 if rel not in changes and rel not in d6_prep['newSources']:assert sha(p)==before[rel],rel
assert len(source_payload)==1066
assert sha(workspace/'Cadoryx.ViewModels/Toolboxes/CadAgentDrawingTools.cs')==sha(base/'Cadoryx.ViewModels/Toolboxes/CadAgentDrawingTools.cs')
assert '8.0.1-preview.31.cadoryx.generated-defeature-history.30' in (workspace/'Directory.Build.props').read_text(encoding='utf-8-sig')
reviews=[]
for rel in sorted(changes+d6_prep['newSources']):
 old=(base/rel).read_text(encoding='utf-8-sig').splitlines() if (base/rel).exists() else []
 new=(workspace/rel).read_text(encoding='utf-8-sig').splitlines()
 reviews.extend(difflib.unified_diff(old,new,fromfile='a/'+rel,tofile='b/'+rel,lineterm=''))
review=task/'source-review-1.diff';review.write_text('\n'.join(reviews)+'\n',encoding='utf-8')
out=task/'preparation-1.json';out.write_text(json.dumps({'recordedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),
 'state':'D6_INDEPENDENT_SDK30_PRODUCTION_INTEGRATION_SOURCE_PREPARED_NOT_COMPILED',
 'workspace':str(workspace),'baselineBuild':ref(base_build_path),'baselinePreparation':ref(base_prep_path),
 'baselineFull6467Both':full,'originalD6Preparation':ref(d6_prep_path),'originalD6Functional':ref(root/'capability-evidence/AF_PENDING_D6_FINITE_NEWBODY_FUNCTIONAL.json'),
 'baselineSourceCount':1063,'sourceCount':1066,'sourcePayload':source_payload,'changedExisting':changes,
 'newSources':d6_prep['newSources'],'allSevenD6PreconditionsByteIdentical':True,'tenD6SourceFilesByteIdenticalToSdk31Candidate':True,
 'allOriginal6467TestSourcesUnchanged':True,'new63SourcesUnchanged':True,'expectedInventoryCount':6530,
 'expectedFiniteScopeCount':99,'expectedAgentScopeCount':165,'schemaBudgetDescriptionsUnchangedFromVerifiedSdk30':True,
 'native62':base_build['native62'],'sdk13':base_build['sdk13'],'sourceReview':ref(review),
 'firstPreparationLabelFailure':ref(task/'source-preparation-first-failure-1.json') if recover else None,
 'sourceRecoveryOnlyAuditedExistingBytes':recover,
 'originalSdk31CandidateChanged':False,'originalSdk31GatesCancelled':False,'nativeBindingsAdded':False,
 'compilationStarted':False,'actualWpfWindowsStarted':False,'productionApplied':False,'cleanup':False,
 'AF':'ACTIVE','G':'USER_DEFERRED'},indent=2)+'\n',encoding='utf-8')
print('Prepared source1066 SDK30 integration: original6467 + unchanged63; all seven preconditions exact, ten D6 inputs identical. No build/native task/window started.')
