"""Retain candidate5's real failed terminal without rerunning it or expanding deferrals."""
from pathlib import Path
from collections import Counter
from datetime import datetime, timezone
import hashlib, json, xml.etree.ElementTree as ET

ROOT=Path(__file__).resolve().parents[2];STAGE=ROOT/'artifacts/af-final87-5'
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref=lambda p:dict(path=p.relative_to(ROOT).as_posix(),sha256=sha(p))
freeze=read(STAGE/'freeze.json');out=STAGE/'full-release-1'
terminal=read(out/'exit.json');assert terminal['exitCode']==1 and terminal['actualNativeBinding'] and not terminal['STEP24Retried']
tree=ET.parse(out/'runtime.trx').getroot();ns={'t':tree.tag.split('}')[0].strip('{')}
results=tree.findall('.//t:UnitTestResult',ns)
assert len(results)==7174 and Counter(r.attrib['outcome'] for r in results)=={'Passed':7172,'Failed':2}
expected=Counter(freeze['configurations'][0]['discoveredNames'])
for name in freeze['deferredNames']:assert expected[name]==1;del expected[name]
assert Counter(r.attrib['testName'] for r in results)==expected
failures=[]
for result in results:
    if result.attrib['outcome']!='Failed':continue
    name=result.attrib['testName']
    assert name.startswith('Cadoryx.Tests.DrawingInspectionTypeFeedbackTests.DefaultTwelveToolsAnd8192ContextBudgetKeepTheExplicitInspectionRequirement(')
    message=result.find('t:Output/t:ErrorInfo/t:Message',ns).text
    assert 'type+documentId+stateId' in message
    failures.append(dict(name=name,message=message,stack=result.find('t:Output/t:ErrorInfo/t:StackTrace',ns).text))
binding=read(out/'loaded-native.json')
assert len(binding['native'])==62 and {r['name']:r['sha256'] for r in binding['native']}=={r['name']:r['sha256'] for r in freeze['native62']}
assert not (STAGE/'full-debug-1').exists() and not (STAGE/'full-both-exit.json').exists()
assert not (STAGE/'after-full-exit.json').exists()
record=dict(recordedAt=datetime.now(timezone.utc).isoformat(),state='CANDIDATE5_RELEASE_FAILED_TWO_INSPECT_DESCRIPTION_REGRESSIONS',
    freeze=ref(STAGE/'freeze.json'),terminal=ref(out/'exit.json'),trx=ref(out/'runtime.trx'),native=ref(out/'loaded-native.json'),
    passed=7172,failed=2,skipped=0,failures=failures,exactSTEP24NotExecuted=True,additionalFailuresDeferred=False,
    debugStarted=False,modelOrWindowFollowupStarted=False,originalAssertionsChanged=False,AFComplete=False)
target=ROOT/'capability-evidence/diagnostics/af-final87/candidate5-failed-terminal.json';assert not target.exists()
target.write_text(json.dumps(record,indent=2)+'\n',encoding='utf-8')
print('Candidate5 actual7172PASS/2FAIL, exact24 not executed; failed source retained, Debug/followups not started.')
