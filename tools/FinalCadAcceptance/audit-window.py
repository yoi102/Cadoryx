"""Audit original D6 control trace assertions with final87 provenance."""
from pathlib import Path
from datetime import datetime,timezone
import argparse,hashlib,json,subprocess,sys
ROOT=Path(__file__).resolve().parents[2]
parser=argparse.ArgumentParser();parser.add_argument('case_index',type=int);parser.add_argument('--candidate',type=int,default=3);parser.add_argument('--iteration',type=int,default=1)
args=parser.parse_args();index=args.case_index
assert 0<=index<36 and 1<=args.candidate<=100 and 1<=args.iteration<=100
DRAFT=ROOT/f'artifacts/af-final87-{args.candidate}'
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref=lambda p:dict(path=p.relative_to(ROOT).as_posix(),sha256=sha(p))
subprocess.run([sys.executable,str(Path(__file__).with_name('require-followup-runtime.py')),str(args.candidate)],check=True)
freeze=read(DRAFT/'freeze.json');prep=read(DRAFT/'followup-preparation.json')
assert prep['freeze']['sha256']==sha(DRAFT/'freeze.json') and len(prep['D6Matrix'])==36
case=dict(prep['D6Matrix'][index])
if args.iteration!=1:
 assert case['output'].endswith('-1');case['output']=case['output'][:-1]+str(args.iteration)
assert sha(Path(case['fixture']['path']))==case['fixture']['sha256']
host_path=DRAFT/'hosts-verification.json';hosts=read(host_path)
assert hosts['freeze']['sha256']==sha(DRAFT/'freeze.json') and hosts['readinessSeconds']==30 and hosts['D6DeadlineSeconds']==300
host=next(r for r in hosts['hosts'] if r['label']=='d6-window36' and r['configuration']=='Release')
candidate=Path(host['output'])
for row in host['payload']:assert sha(candidate/row['path'])==row['sha256']
for row in prep['hostSources']:assert sha(ROOT/row['path'])==row['sha256']
output = Path(case['output'])
terminal_path = Path(str(output) + '-exit.json'); terminal = read(terminal_path)
assert terminal['state'] == 'PROCESS_EXIT0_PENDING_INDEPENDENT_TRACE_AUDIT_AND_DESKTOP_REVIEW'
assert terminal['exitCode'] == 0 and terminal['caseIndex'] == index and terminal['case'] == case
assert terminal['readinessSeconds'] == 30 and terminal['observationSeconds'] == 300
assert Path(terminal['candidate']) == candidate and not terminal['hostInjectsInput']
result_path = output / 'result.json'; result = read(result_path)
assert result['state'] == 'D6_PRODUCTION_WINDOW_CONTROL_TRACE_AND_DOCUMENT_CHECKS_COMPLETE_PENDING_DESKTOP_REVIEW'
for key in ('language', 'route', 'phase', 'label'): assert result[key] == case[key]
required = {'INVENTORY_CLEARED_EXPLICIT_SELECTION', 'MISSING_LIMIT_PREVIEW_REJECTED',
            'NEAR_PREVIEW_READY', 'BRANCH_INVALIDATED_READY_PREVIEW',
            'FAR_PREVIEW_REJECTED', 'NEAR_PREVIEW_RESTORED'}
assert len(result['evidence']) == 6 and set(result['evidence']) == required
assert result['assetsRemaining'] == 0 and not result['hostInjectedInput']
assert result['externalDesktopReviewRequired'] and not result['performanceAcceptance']
assert not result['productionApplied'] and result['AF'] == 'ACTIVE'
saved = Path(result['saved']); assert saved.parent == output and sha(saved) == result['sha256']
assert saved.stat().st_size > 0 and (output / 'bindings.log').stat().st_size == 0
expected = {r['name']: r['sha256'] for r in freeze['native62']}
binding_path = Path(str(output) + '-loaded-native.json')
for native in (result['native'], read(binding_path)['native']):
    assert len(native) == 62 and {r['name']: r['sha256'] for r in native} == expected
    for row in native:
        assert Path(row['path']).parent == candidate / 'occt' and sha(Path(row['path'])) == row['sha256']
out = Path(str(output) + '-verification.json'); assert not out.exists()
out.write_text(json.dumps({'recordedAt': datetime.now(timezone.utc).isoformat(),
    'state': 'FINAL87_D6_ORIGINAL_CASE_TRACE_DOCUMENT_NATIVE62_VERIFIED_DESKTOP_REVIEW_PENDING',
    'caseIndex': index, 'case': case, 'freeze': ref(DRAFT / 'freeze.json'),
    'host': ref(host_path), 'terminal': ref(terminal_path), 'result': ref(result_path),
    'binding': ref(binding_path), 'savedDocument': ref(saved),
    'originalSixControlStatesAnd30_300sRetained': True, 'assetsRemaining': 0,
    'desktopReview': 'PENDING_ACTUAL_RETURNED_MODAL_AND_INPUT_EVIDENCE',
    'hostInjectedInput': False, 'productionApplied': False, 'AFComplete': False}, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'caseIndex': index, 'traceAndDocumentVerified': True, 'desktopReview': 'PENDING'}))
