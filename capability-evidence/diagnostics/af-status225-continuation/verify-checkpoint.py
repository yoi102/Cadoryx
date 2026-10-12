from pathlib import Path
import datetime,hashlib,json,subprocess
root=Path('C:/Users/yoiri/source/repos/Cadoryx')
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
names=(
 'AF_PENDING_D6_SDK30_INTEGRATION_SOURCE.json','AF_PENDING_D6_SDK30_INTEGRATION_FUNCTIONAL.json',
 'AF_PENDING_D6_WINDOW_OBSERVER_BUILD_VERIFICATION.json','AF_PENDING_D6_WINDOW_OBSERVER_V3_BUILD_VERIFICATION.json',
 'AF_PENDING_F5_SOURCE1070_FULL_PROVIDER_VERIFICATION.json',
 'AF_PENDING_F5_DRAWING_RECOVERY_CONTEXT_DIAGNOSTIC.json','AF_PENDING_F5_DRAWING_RECOVERY_CONTEXT_DIAGNOSTIC_V2.json',
 'AF_PENDING_SDK31_EXACT_KNOT_SUBDIVISION_REJECTIONS.json','AF_PENDING_STATUS225_CONTINUATION_CHECKPOINT.json')
refs={}
def visit(value):
    if isinstance(value,dict):
        path=value.get('path');hash_value=value.get('sha256')
        if isinstance(path,str) and isinstance(hash_value,str) and Path(path).is_absolute():
            p=Path(path);assert p.is_file(),str(p)
            assert sha(p)==hash_value,(str(p),'reference hash drift')
            refs[str(p)]=hash_value
        for child in value.values():visit(child)
    elif isinstance(value,list):
        for child in value:visit(child)
for name in names:visit(read(root/'capability-evidence'/name))
visit(read(root/'capability-evidence/diagnostics/af-status225-continuation/source-preservation.json'))
functional=read(root/'capability-evidence/AF_PENDING_D6_SDK30_INTEGRATION_FUNCTIONAL.json')
assert len(functional['matrices'])==4 and all(row['failed']==0 and row['skipped']==0 for row in functional['matrices'])
window=read(root/'capability-evidence/AF_PENDING_D6_WINDOW_OBSERVER_V3_BUILD_VERIFICATION.json')
assert len(window['uiMatrix'])==36 and not window['actualWindowsPassed']
knots=read(root/'capability-evidence/AF_PENDING_SDK31_EXACT_KNOT_SUBDIVISION_REJECTIONS.json')
assert len(knots['cases'])==14 and all(not c['productAccepted'] for c in knots['cases'])
staged=subprocess.run(['git','diff','--cached','--name-only','-z'],cwd=root,capture_output=True,check=True).stdout.split(b'\0')
index=[]
for value in staged:
    if not value:continue
    name=value.decode('utf-8')
    if name.startswith('capability-evidence/'):
        actual=subprocess.run(['git','show',':'+name],cwd=root,capture_output=True,check=True).stdout
        assert actual==(root/name).read_bytes(),(name,'index changes evidence bytes')
        index.append(name)
out=root/'artifacts/af-status225-checkpoint-reference-index-verification-1.json';assert not out.exists()
out.write_text(json.dumps({'recordedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),
 'state':'PASS_ALL_NEW_CHECKPOINT_ABSOLUTE_REFERENCE_HASHES_AND_STAGED_EVIDENCE_BYTES',
 'verifiedReferenceCount':len(refs),'verifiedReferences':refs,'stagedByteStableEvidence':index,
 'actualFullRuntimePassed':False,'actualWindowsPassed':False,'productionApplied':False,
 'cleanup':False,'AF':'ACTIVE','G':'USER_DEFERRED'},indent=2)+'\n',encoding='utf-8')
print(f'PASS {len(refs)} absolute evidence reference hashes; {len(index)} staged evidence files preserve bytes.')
