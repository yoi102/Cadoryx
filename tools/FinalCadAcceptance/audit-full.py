"""Prove all nondeferred discovered cases ran with the exact final87 payload."""
from pathlib import Path
from collections import Counter
from datetime import datetime, timezone
import json, hashlib, sys, xml.etree.ElementTree as ET
iteration=int(sys.argv[1]) if len(sys.argv)==2 else 1
assert 1<=iteration<=100
ROOT=Path(__file__).resolve().parents[2]; STAGE=ROOT/f'artifacts/af-final87-{iteration}'
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref=lambda p:dict(path=p.relative_to(ROOT).as_posix(),sha256=sha(p))
freeze=read(STAGE/'freeze.json'); suite=read(STAGE/'full-both-exit.json')
assert freeze['expectedExecuted']==freeze['expectedTotal']-len(freeze['deferredNames'])
assert suite['totalDiscovered']==freeze['expectedTotal'] and suite['executedEach']==freeze['expectedExecuted'] and not suite['focused']
native={r['name']:r['sha256'] for r in freeze['native62']}; cases=[]
assert len(suite['configurations'])==2
for config in freeze['configurations']:
    label=config['configuration']; out=STAGE/f'full-{label.lower()}-1'
    terminal=read(out/'exit.json'); assert terminal['exitCode']==0 and terminal['actualNativeBinding'] and not terminal['STEP24Retried']
    for row in config['payload']: assert sha(Path(config['testDirectory'])/row['path'])==row['sha256']
    tree=ET.parse(out/'runtime.trx').getroot(); ns={'t':tree.tag.split('}')[0].strip('{')}
    rows=tree.findall('.//t:UnitTestResult',ns)
    assert len(rows)==freeze['expectedExecuted'] and all(r.attrib['outcome']=='Passed' for r in rows)
    assert len({r.attrib['testId'] for r in rows})==freeze['expectedExecuted']
    expected=Counter(config['discoveredNames']); assert sum(expected.values())==freeze['expectedTotal']
    for name in freeze['deferredNames']: assert expected[name]==1; del expected[name]
    assert Counter(r.attrib['testName'] for r in rows)==expected
    binding=read(out/'loaded-native.json')
    assert {r['name']:r['sha256'] for r in binding['native']}==native and len(binding['native'])==62
    for row in binding['native']:
        assert Path(row['path']).parent==Path(config['testDirectory'])/'occt' and sha(Path(row['path']))==row['sha256']
    cases.append(dict(configuration=label,passed=freeze['expectedExecuted'],failed=0,skipped=0,exactUserDeferredNotExecuted=24,
        terminal=ref(out/'exit.json'),trx=ref(out/'runtime.trx'),native=ref(out/'loaded-native.json')))
record=dict(recordedAt=datetime.now(timezone.utc).isoformat(),state=f"FINAL87_CURRENT_BOTH_FULL_NONDEFERRED{freeze['expectedExecuted']}_PASS_ACTUAL62",
    freeze=ref(STAGE/'freeze.json'),version=freeze['version'],configurations=cases,
    original7117NamesAndMultiplicityPreserved=True,newCaseCount=freeze['expectedTotal']-7117,exactSTEP24NotExecuted=True,countsAndDisplayNameMultiplicityVerified=True,
    modelOrWindowAccepted=False,productionApplied=False,AFComplete=False)
out=ROOT/f'capability-evidence/diagnostics/af-final87/candidate{iteration}-full-verification.json';out.parent.mkdir(parents=True,exist_ok=True)
if out.exists():
    before=read(out)
    assert {k:v for k,v in before.items() if k!='recordedAt'}=={k:v for k,v in record.items() if k!='recordedAt'}
else:out.write_text(json.dumps(record,indent=2)+'\n',encoding='utf-8')
print(f"Both actual{freeze['expectedExecuted']}/{freeze['expectedExecuted']}, exact24 NOT RUN; original/current discovery multiplicities and actual62 exact; model/window still pending.")
