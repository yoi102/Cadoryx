"""Prove all nondeferred discovered cases ran with the exact final87 payload."""
from pathlib import Path
from collections import Counter
from datetime import datetime, timezone
import json, hashlib, xml.etree.ElementTree as ET
ROOT=Path(__file__).resolve().parents[2]; STAGE=ROOT/'artifacts/af-final87-1'
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref=lambda p:dict(path=p.relative_to(ROOT).as_posix(),sha256=sha(p))
freeze=read(STAGE/'freeze.json'); suite=read(STAGE/'full-both-exit.json')
native={r['name']:r['sha256'] for r in freeze['native62']}; cases=[]
assert len(suite['configurations'])==2
for config in freeze['configurations']:
    label=config['configuration']; out=STAGE/f'full-{label.lower()}-1'
    terminal=read(out/'exit.json'); assert terminal['exitCode']==0 and terminal['actualNativeBinding'] and not terminal['STEP24Retried']
    for row in config['payload']: assert sha(Path(config['testDirectory'])/row['path'])==row['sha256']
    tree=ET.parse(out/'runtime.trx').getroot(); ns={'t':tree.tag.split('}')[0].strip('{')}
    rows=tree.findall('.//t:UnitTestResult',ns)
    assert len(rows)==7168 and all(r.attrib['outcome']=='Passed' for r in rows)
    assert len({r.attrib['testId'] for r in rows})==7168
    expected=Counter(config['discoveredNames']); assert sum(expected.values())==7192
    for name in freeze['deferredNames']: assert expected[name]==1; del expected[name]
    assert Counter(r.attrib['testName'] for r in rows)==expected
    binding=read(out/'loaded-native.json')
    assert {r['name']:r['sha256'] for r in binding['native']}==native and len(binding['native'])==62
    for row in binding['native']:
        assert Path(row['path']).parent==Path(config['testDirectory'])/'occt' and sha(Path(row['path']))==row['sha256']
    cases.append(dict(configuration=label,passed=7168,failed=0,skipped=0,exactUserDeferredNotExecuted=24,
        terminal=ref(out/'exit.json'),trx=ref(out/'runtime.trx'),native=ref(out/'loaded-native.json')))
record=dict(recordedAt=datetime.now(timezone.utc).isoformat(),state='FINAL87_CURRENT_BOTH_FULL_NONDEFERRED7168_PASS_ACTUAL62',
    freeze=ref(STAGE/'freeze.json'),version=freeze['version'],configurations=cases,
    original7117AndNew75Discovered=True,exactSTEP24NotExecuted=True,countsAndDisplayNameMultiplicityVerified=True,
    modelOrWindowAccepted=False,productionApplied=False,AFComplete=False)
out=ROOT/'capability-evidence/diagnostics/af-final87/full-verification.json';out.parent.mkdir(parents=True,exist_ok=True)
assert not out.exists();out.write_text(json.dumps(record,indent=2)+'\n',encoding='utf-8')
print('Both actual7168/7168, exact24 NOT RUN; original/current discovery multiplicities and actual62 exact; model/window still pending.')
