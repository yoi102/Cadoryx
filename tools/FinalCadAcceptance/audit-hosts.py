"""Check six immutable hosts against the exact final application and inputs."""
from pathlib import Path
from datetime import datetime, timezone
import json, hashlib, re, sys
iteration=int(sys.argv[1]) if len(sys.argv)==2 else 1
assert 1<=iteration<=100
ROOT=Path(__file__).resolve().parents[2]; STAGE=ROOT/f'artifacts/af-final87-{iteration}'
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref=lambda p:dict(path=p.relative_to(ROOT).as_posix(),sha256=sha(p))
freeze=read(STAGE/'freeze.json');prep=read(STAGE/'followup-preparation.json');built=read(STAGE/'hosts-build-exit.json')
assert prep['freeze']['sha256']==sha(STAGE/'freeze.json')
assert len(built['stages'])==6 and all(r['exitCode']==0 for r in built['stages'])
for row in prep['hostSources']:assert sha(ROOT/row['path'])==row['sha256']
sdk={r['name']:r['sha256'] for r in freeze['managed13']}; native={r['name']:r['sha256'] for r in freeze['native62']}
hosts=[]
for stage in built['stages']:
    config=next(c for c in freeze['configurations'] if c['configuration']==stage['configuration'])
    app=Path(config['candidate']);folder=Path(stage['output']);assert Path(stage['candidate'])==app
    log=Path(stage['log']).read_text(encoding='utf-8-sig');assert not re.search(r'\b(?:warning|error)\s+(?:[A-Z]+\d+|:)',log,re.I)
    for row in config['appPayload']:assert sha(app/row['path'])==row['sha256']
    if stage['label']!='main-status36':
        production=list(app.glob('Cadoryx.*.dll'));assert len(production)==19
        for path in production:assert sha(folder/path.name)==sha(path)
        assert {p.name:sha(p) for p in folder.glob('OcctSharp*.dll')}==sdk
        assert {p.name:sha(p) for p in (folder/'occt').glob('*.dll')}==native
        for path in app.glob('*/Cadoryx.Lang.resources.dll'):assert sha(folder/path.parent.name/path.name)==sha(path)
    hosts.append(dict(stage,payload=[dict(path=p.relative_to(folder).as_posix(),sha256=sha(p)) for p in sorted(folder.rglob('*')) if p.is_file()]))
record=dict(recordedAt=datetime.now(timezone.utc).isoformat(),state='FINAL87_SIX_RECOVERY_AND_D6_HOSTS_BUILD0_PAYLOAD_VERIFIED_NOT_RUNTIME',
    freeze=ref(STAGE/'freeze.json'),followup=ref(STAGE/'followup-preparation.json'),hosts=hosts,
    readinessSeconds=30,recoveryDeadlineSeconds=900,D6DeadlineSeconds=300,
    original24Saves=48,original24Images=52,main36Images=144,D6MatrixCount=36,
    modelRuntimeNotProved=True,recoveryRuntimeStarted=False,desktopInputStarted=False,AFComplete=False)
out=STAGE/'hosts-verification.json'
if out.exists():
    before=read(out)
    assert {k:v for k,v in before.items() if k!='recordedAt'}=={k:v for k,v in record.items() if k!='recordedAt'}
else:out.write_text(json.dumps(record,indent=2)+'\n',encoding='utf-8')
print('Six host builds and exact app19/SDK13/native62/resource payloads verified; original conditions kept; runtime NOT RUN.')
