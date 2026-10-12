from pathlib import Path
import datetime,hashlib,json,os,shutil,subprocess
root=Path('C:/Users/yoiri/source/repos/Cadoryx');task=root/'artifacts/d6-sdk30-production-integration-1'
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'));sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper();ref=lambda p:{'path':str(p),'sha256':sha(p)}
prep_path=task/'preparation-1.json';prep=read(prep_path);workspace=Path(prep['workspace'])
base_prep=read(Path(prep['baselinePreparation']['path']));base=Path(base_prep['workspace'])
original_patch=root/'capability-evidence/AF_PENDING_D6_FINITE_NEWBODY.patch'
data=original_patch.read_bytes();blocks=data.split(b'diff --git ');assert blocks[0]==b'' and len(blocks)==12
retained=[b'diff --git '+b for b in blocks[1:] if not b.startswith(b'a/Cadoryx.ViewModels/Toolboxes/CadAgentDrawingTools.cs ')]
assert len(retained)==10
patch=root/'capability-evidence/AF_PENDING_D6_SDK30_INTEGRATION.patch';assert not patch.exists();patch.write_bytes(b''.join(retained))
replay=task/'source-recovery-replay-1';assert not replay.exists();replay.mkdir()
for row in base_prep['sourcePayload']:
 source=base/row['path'];assert sha(source)==row['sha256'];dest=replay/row['path'];dest.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(source,dest)
env=dict(os.environ);env['GIT_CEILING_DIRECTORIES']=str(replay.parent);checks=[]
for extra in (['--check'],[]):
 p=subprocess.run(['git','-c','core.autocrlf=false','apply','--no-index',*extra,str(patch)],cwd=replay,env=env,capture_output=True,text=True)
 checks.append({'arguments':extra,'exitCode':p.returncode,'stdout':p.stdout,'stderr':p.stderr});assert p.returncode==0
for row in prep['sourcePayload']:
 assert sha(workspace/row['path'])==sha(replay/row['path'])==row['sha256'],row['path']
review=root/'capability-evidence/AF_PENDING_D6_SDK30_INTEGRATION_REVIEW.diff';assert not review.exists();shutil.copy2(Path(prep['sourceReview']['path']),review)
out=root/'capability-evidence/AF_PENDING_D6_SDK30_INTEGRATION_SOURCE.json';assert not out.exists()
out.write_text(json.dumps({'recordedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),
 'state':'PASS_D6_SDK30_TEN_SOURCE_PATCH_REPLAY_ALL1066_BUILD_RUNTIME_WINDOWS_NOT_RUN',
 'candidate':str(workspace),'sourceCount':1066,'expectedInventoryCount':6530,'original6467TestSourcesUnchanged':True,'new63SourcesUnchanged':True,
 'preparation':ref(prep_path),'baselineBuild':prep['baselineBuild'],'baselinePreparation':prep['baselinePreparation'],
 'baselineFull6467Both':prep['baselineFull6467Both'],'originalD6Functional':prep['originalD6Functional'],
 'originalSdk31Patch':ref(original_patch),'patch':ref(patch),'reviewDiff':ref(review),
 'retainedSources':prep['changedExisting']+prep['newSources'],'allSevenD6PreconditionsByteIdentical':True,
 'sourcePayload':prep['sourcePayload'],'sourceReplay':str(replay),'replayChecks':checks,'all1066ReplayVerified':True,
 'firstSourcePreparationLabelFailure':prep['firstPreparationLabelFailure'],'sourceCorrectionOnlyAuditedExistingBytes':True,
 'compilationStarted':False,'full6530':False,'actualWpfWindows':False,'originalSdk31CandidateChanged':False,
 'originalSdk31GatesCancelled':False,'applicationExportGuardChanged':False,'nativeBindingsAdded':False,
 'publicUpstreamModified':False,'productionApplied':False,'cleanup':False,'AF':'ACTIVE','G':'USER_DEFERRED'},indent=2)+'\n',encoding='utf-8')
print('PASS ten exact D6 source blocks independently replayed all1066 on verified SDK30 base; actual compile/full6530/windows remain NOT RUN.')
