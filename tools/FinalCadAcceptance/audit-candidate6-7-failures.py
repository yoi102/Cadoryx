"""Retain the warning gate and expanded regression failures as distinct sources."""
from pathlib import Path
from collections import Counter
from datetime import datetime, timezone
import hashlib, json, xml.etree.ElementTree as ET

ROOT=Path(__file__).resolve().parents[2]
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref=lambda p:dict(path=p.relative_to(ROOT).as_posix(),sha256=sha(p))
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
six=ROOT/'artifacts/af-final87-6';seven=ROOT/'artifacts/af-final87-7'
assert not (six/'freeze.json').exists()
build=read(six/'build-exit.json');assert all(r['exitCode']==0 for r in build['stages'])
warningRows=[]
for config in ('release','debug'):
    log=six/f'build-{config}.log';text=log.read_text(encoding='utf-8-sig')
    assert '16 Warning(s)' in text and '0 Error(s)' in text
    assert 'GHSA-2fmv-rv2c-59x7' in text and 'GHSA-846f-f764-76mx' in text
    warningRows.append(dict(configuration=config,compileExit=0,warnings=16,zeroWarningGatePassed=False,log=ref(log)))
freeze=read(seven/'freeze.json');output=seven/'focused-release-1'
terminal=read(output/'exit.json');assert terminal['exitCode']==1 and terminal['actualNativeBinding'] and not terminal['STEP24Retried']
tree=ET.parse(output/'runtime.trx').getroot();ns={'t':tree.tag.split('}')[0].strip('{')}
rows=tree.findall('.//t:UnitTestResult',ns)
assert len(rows)==291 and Counter(r.attrib['outcome'] for r in rows)=={'Passed':288,'Failed':3}
classes={'NativeOmittedGeometryReadTests','NativeCompleteSourceContextTests','AgentNativeSelectionRetentionTests',
    'LmStudioCompletionIntegrityTests','AgentEmptyResponseRecoveryTests','AgentOpaqueIdentityBudgetTests','AgentContextTests',
    'DrawingContractRequestGuideTests','DrawingAgentTests','DrawingInspectionTypeFeedbackTests','ExchangeOccurrenceSemanticTests','ExchangeMeasurementTests'}
expected=Counter(n for n in freeze['configurations'][0]['discoveredNames'] if n.split('.')[2] in classes)
assert Counter(r.attrib['testName'] for r in rows)==expected
failures=[]
for row in rows:
    if row.attrib['outcome']=='Failed':
        failures.append(dict(name=row.attrib['testName'],message=row.find('t:Output/t:ErrorInfo/t:Message',ns).text,
            stack=row.find('t:Output/t:ErrorInfo/t:StackTrace',ns).text))
assert sum(f['name'].startswith('Cadoryx.Tests.DrawingInspectionTypeFeedbackTests.') for f in failures)==2
assert sum(f['name'].startswith('Cadoryx.Tests.AgentEmptyResponseRecoveryTests.RecoveryDirectiveRetainsAllTwelveTools') for f in failures)==1
native=read(output/'loaded-native.json')
assert len(native['native'])==62 and {r['name']:r['sha256'] for r in native['native']}=={r['name']:r['sha256'] for r in freeze['native62']}
for prefix,count in [('ExchangeMeasurementTests',39),('ExchangeOccurrenceSemanticTests',10)]:
    matches=[r for r in rows if r.attrib['testName'].startswith('Cadoryx.Tests.'+prefix+'.')]
    assert len(matches)==count and all(r.attrib['outcome']=='Passed' for r in matches)
assert not (seven/'focused-debug-1').exists() and not (seven/'full-release-1').exists()
record=dict(recordedAt=datetime.now(timezone.utc).isoformat(),state='CANDIDATE6_WARNING_GATE_AND_CANDIDATE7_THREE_ORIGINAL_REGRESSIONS_RETAINED',
    candidate6=dict(preparation=ref(six/'preparation.json'),build=ref(six/'build-exit.json'),warnings=warningRows,frozen=False,runtimeStarted=False),
    candidate7=dict(freeze=ref(seven/'freeze.json'),trx=ref(output/'runtime.trx'),terminal=ref(output/'exit.json'),native=ref(output/'loaded-native.json'),
        passed=288,failed=3,skipped=0,failures=failures,metadata10Passed=True,originalNativeMeasurement39Passed=True,debugStarted=False,fullStarted=False),
    failuresDeferred=False,assertionsOrBudgetsChanged=False,STEP24Retried=False,AFComplete=False)
out=ROOT/'capability-evidence/diagnostics/af-final87/candidate6-7-failed-terminals.json';assert not out.exists()
out.write_text(json.dumps(record,indent=2)+'\n',encoding='utf-8')
print('6: warning gate rejected;7: enlarged Release288/291; original failures retained, no full/model/window start.')
