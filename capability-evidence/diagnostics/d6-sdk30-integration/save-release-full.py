from pathlib import Path
import datetime,hashlib,json,xml.etree.ElementTree as ET
root=Path('C:/Users/yoiri/source/repos/Cadoryx')
draft=root/'artifacts/d6-sdk30-production-integration-1'
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref=lambda p:{'path':str(p),'sha256':sha(p)}
audit_path=draft/'full6530-release-1-verification.json'
audit=read(audit_path)
assert audit['configuration']=='Release' and audit['group']=='full6530'
assert audit['count']==audit['passed']==6530 and audit['failed']==audit['skipped']==0
assert audit['allOriginalNamesAndAssertionsUnchanged'] and not audit['originalBudgetsChanged']
for field in ('build','terminal','trx','loadedNative'):
    row=audit[field];assert sha(Path(row['path']))==row['sha256']
build=read(Path(audit['build']['path']));assert build['sourceCount']==1066
assert audit['names']==build['configurations']['Release']['inventoryNames']
assert len(audit['names'])==len(set(audit['names']))==6530
runtime=Path(build['configurations']['Release']['testDirectory'])
for row in build['sourcePayload']:assert sha(Path(build['workspace'])/row['path'])==row['sha256']
for row in build['configurations']['Release']['testPayload']:assert sha(runtime/row['path'])==row['sha256']
binding=read(Path(audit['loadedNative']['path']))
assert len(binding['native'])==62
assert {r['name']:r['sha256'] for r in binding['native']}=={r['name']:r['sha256'] for r in build['native62']}
for row in binding['native']:
    p=Path(row['path']);assert p.parent.resolve()==(runtime/'occt').resolve() and sha(p)==row['sha256']
tree=ET.parse(Path(audit['trx']['path']));ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
rows=tree.findall('.//t:UnitTestResult',ns)
assert len(rows)==6530 and all(r.attrib['outcome']=='Passed' for r in rows)
assert set(r.attrib['testName'] for r in rows)==set(audit['names'])
terminal=read(Path(audit['terminal']['path']));assert terminal['exitCode']==0
out=root/'capability-evidence/AF_PENDING_D6_SDK30_INTEGRATION_FULL_RELEASE.json'
assert not out.exists()
out.write_text(json.dumps({'recordedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'state':'PASS_SOURCE1066_SDK30_RELEASE_FULL6530_DEBUG_FULL_AND_REAL_WINDOWS_PENDING',
    'sourceCount':1066,'sdkVersion':build['sdkVersion'],'count':6530,'passed':6530,'failed':0,'skipped':0,
    'audit':ref(audit_path),'build':audit['build'],'terminal':audit['terminal'],'trx':audit['trx'],
    'loadedNative':audit['loadedNative'],'loadedNativeCount':62,
    'started':terminal['started'],'completed':terminal['completed'],
    'frozenSourceAndRuntimeRechecked':True,'allOriginalNamesAndAssertionsUnchanged':True,
    'originalBudgetsChanged':False,'debugFullPassed':False,'actualWindowsPassed':False,
    'sdk31FullOrProviderAccepted':False,'performanceAcceptance':False,'productionApplied':False,
    'cleanup':False,'AF':'ACTIVE','G':'USER_DEFERRED'},indent=2)+'\n',encoding='utf-8')
print('PASS Release full6530/6530 original names, source1066 and actual native62; Debug and real windows pending.')
