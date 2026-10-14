"""Preserve original follow-up inputs and bind them to the final87 candidate."""
from pathlib import Path
from datetime import datetime, timezone
import json, hashlib, sys
iteration=int(sys.argv[1]) if len(sys.argv)==2 else 1
assert 1<=iteration<=100
ROOT=Path(__file__).resolve().parents[2];STAGE=ROOT/f'artifacts/af-final87-{iteration}'
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref=lambda p:dict(path=p.relative_to(ROOT).as_posix(),sha256=sha(p))
freeze_path=STAGE/'freeze.json';freeze=read(freeze_path);work=Path(freeze['workspace'])
old=read(ROOT/'artifacts/af1082-f5-d7-local87cu1-1/followup-preparation.json')
models=[]
for row in old['originalModelPromptAndAssertionSources']:
    path=ROOT/row['path'];candidate=work/'tools/CadAgentWorkflowProbe'/path.name
    assert sha(path)==sha(candidate)==row['sha256'];models.append(ref(candidate))
assert len(models)==3
perf=read(ROOT/'artifacts/af1082-f5-d7-local87cu1-1/performance-freeze.json')
for key in ('input','expectedImage','originalInstrumentedVerifier'):
    assert sha(Path(perf[key]['path']))==perf[key]['sha256']
assert perf['originalDirectRuns']==2 and perf['originalLifecycleCyclesPerRun']==20 and perf['targetMs']==6482
assert perf['directTimeoutSeconds']==1800 and perf['productionOpenRuns']==2 and perf['productionOpenTimeoutSeconds']==900
matrix=[]
for original in old['D6Matrix']:
    row=dict(original);assert sha(Path(row['fixture']['path']))==row['fixture']['sha256']
    row['output']=str(STAGE/'windows'/Path(row['output']).name);matrix.append(row)
assert len(matrix)==36
recovery=read(ROOT/'artifacts/af1082-f5-d7-local87cu1-1/recovery-preparation.json')
assert recovery['original24']==24 and recovery['original36Matrix']['planned']==36
for row in recovery['sources']:
    for key in ('source','candidate'): assert sha(ROOT/row[key]['path'])==row[key]['sha256']
hosts=[('recovery24','tools/CadRecoveryWindowProbe/ViewportRecovery/WpfAutoRecoveryConsumer.csproj'),
    ('main-status36','tools/CurrentCadCandidate/MainStatusRecoveryRevision/MainStatusRecoveryRevision.csproj'),
    ('d6-window36','tools/CadFiniteSurfaceWindowProbe/WindowHost.csproj')]
sources=[]
for _,project in hosts:
    for path in (work/project).parent.glob('*'):
        if path.is_file() and path.suffix in ('.cs','.csproj'):sources.append(ref(path))
record=dict(recordedAt=datetime.now(timezone.utc).isoformat(),state='FINAL87_ORIGINAL_MODEL_RECOVERY_WINDOW_AND_PERFORMANCE_PREPARED_NOT_RUNTIME',
    freeze=ref(freeze_path),models=models,model='prism-ml/bonsai-27b',variant='prism-ml/bonsai-27b@q1_0',
    endpoint='http://127.0.0.1:15630/v1',context=8192,toolRounds=16,perTurnMinutes=4,
    D6Matrix=matrix,recovery=recovery,hostProjects=hosts,hostSources=sources,
    performance={k:perf[k] for k in ('input','expectedImage','originalInstrumentedVerifier','originalDirectRuns',
        'originalLifecycleCyclesPerRun','targetMs','directTimeoutSeconds','productionOpenRuns','productionOpenTimeoutSeconds')},
    originalAssertionsBudgetsAndSourcesRetained=True,original24Saves=48,original24Images=52,main36Images=144,
    requiredFullEach=freeze['expectedExecuted'],requireBothFullExact24DeferredBeforeRuntime=True,desktopInputStarted=False,
    modelStarted=False,windowStarted=False,performanceStarted=False,AFComplete=False)
out=STAGE/'followup-preparation.json';assert not out.exists();out.write_text(json.dumps(record,indent=2)+'\n',encoding='utf-8')
print('Original models/8192/16/4min, recovery24/36, D6matrix36 and MiniPC2x20/6482ms/full PNG retained; runtime NOT RUN.')
