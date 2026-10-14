"""Bind metadata-only report tests, dependency audit and optional native regression."""
from pathlib import Path
from collections import Counter
from datetime import datetime, timezone
import hashlib, json, shutil, sys, xml.etree.ElementTree as ET

ROOT=Path(__file__).resolve().parents[2]
SOURCE=ROOT/'artifacts/f4-occurrence-semantics-1/local-combined-1'
DEST=ROOT/'capability-evidence/diagnostics/f4-occurrence-semantics'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref=lambda p:dict(path=p.relative_to(ROOT).as_posix(),sha256=sha(p))
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
def rows(path):
    root=ET.parse(path).getroot()
    return root.findall('.//t:UnitTestResult',{'t':root.tag.split('}')[0].strip('{')})
DEST.mkdir(parents=True,exist_ok=True)
expected=Counter(read(ROOT/'artifacts/af-final87-6-root/additional-discovery.json')['additionalNames'])
configs=[]
for config in ('Release','Debug'):
    original=SOURCE/config.lower()/f'semantic-{config.lower()}.trx'
    target=DEST/original.name;assert not target.exists();shutil.copyfile(original,target)
    tests=rows(target)
    assert len(tests)==10 and all(r.attrib['outcome']=='Passed' for r in tests)
    assert Counter(r.attrib['testName'] for r in tests)==expected
    configs.append(dict(configuration=config,passed=10,failed=0,skipped=0,trx=ref(target),nativeExecuted=False))
audit=ROOT/'artifacts/af-final87-6-root/vulnerability-all54-fixed.json'
packages=read(audit);assert len(packages['projects'])==54 and all(not p.get('frameworks') for p in packages['projects'])
target=DEST/'dependency-audit54.json';assert not target.exists();shutil.copyfile(audit,target)
record=dict(recordedAt=datetime.now(timezone.utc).isoformat(),state='REPORT_SCHEMA2_METADATA_CASES_BOTH10_AND_NO_REPORTED_DEPENDENCY_VULNERABILITIES',
    configurations=configs,reportSchema=2,oldSchema1VerifiedWithOriginalRows=True,
    newAspects=['OccurrenceDefinitionSharing','OccurrenceTranslationsMm','OccurrenceOrientations'],
    noGuessedGraphCorrespondence=True,quaternionSignEquivalent=True,dependencyAudit=ref(target),
    patchedPkcs='8.0.2',sdkChanged=False,externalCADVerified=False,completeGeometryEquivalenceProved=False,AFComplete=False)
if len(sys.argv)==2:
    iteration=int(sys.argv[1]);stage=ROOT/f'artifacts/af-final87-{iteration}'
    focused=read(ROOT/f'capability-evidence/diagnostics/af-final87/candidate{iteration}-focused-verification.json')
    record['nativeRegression']=[]
    for config in ('Release','Debug'):
        trx=stage/f'focused-{config.lower()}-1/runtime.trx'
        tests=[r for r in rows(trx) if r.attrib['testName'].startswith('Cadoryx.Tests.ExchangeMeasurementTests.')]
        assert len(tests)==39 and all(r.attrib['outcome']=='Passed' for r in tests)
        record['nativeRegression'].append(dict(configuration=config,originalNativeMeasurementCasesPassed=39,trx=ref(trx)))
    record['frozenCandidateFocused']=ref(ROOT/f'capability-evidence/diagnostics/af-final87/candidate{iteration}-focused-verification.json')
    record['state']='REPORT_SCHEMA2_BOTH_METADATA10_AND_ORIGINAL_NATIVE_MEASUREMENT39_PASS'
out=DEST/'verification.json';assert not out.exists()
out.write_text(json.dumps(record,indent=2)+'\n',encoding='utf-8')
print(record['state'])
