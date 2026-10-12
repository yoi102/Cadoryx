from pathlib import Path
import argparse,datetime,hashlib,json,xml.etree.ElementTree as ET
p=argparse.ArgumentParser()
p.add_argument('--draft',required=True)
p.add_argument('--configuration',choices=['Release','Debug'],required=True)
p.add_argument('--group',choices=['finite99','agent165','full6530','full6556','full6619'],required=True)
p.add_argument('--attempt',type=int,required=True)
a=p.parse_args()
root=Path('C:/Users/yoiri/source/repos/Cadoryx');draft=root/'artifacts'/a.draft
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref=lambda p:{'path':str(p),'sha256':sha(p)}
build_path=draft/'build-both-1-verification.json';build=read(build_path)
cfg=build['configurations'][a.configuration];workspace=Path(build['workspace']);runtime=Path(cfg['testDirectory'])
for row in build['sourcePayload']:assert sha(workspace/row['path'])==row['sha256'],row['path']
for row in cfg['testPayload']:assert sha(runtime/row['path'])==row['sha256'],row['path']
expected=cfg['relatedNames'] if a.group=='finite99' else cfg['agentNames'] if a.group=='agent165' else cfg['inventoryNames']
count={'finite99':99,'agent165':165,'full6530':6530,'full6556':6556,'full6619':6619}[a.group]
assert len(expected)==len(set(expected))==count
run=draft/f'{a.group}-{a.configuration.lower()}-{a.attempt}'
terminal=read(run/'exit.json');ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
tree=ET.parse(run/'runtime.trx');rows=tree.findall('.//t:UnitTestResult',ns)
assert len(rows)==count and set(r.attrib['testName'] for r in rows)==set(expected)
counts={s:sum(r.attrib['outcome']==s for r in rows) for s in ('Passed','Failed','NotExecuted')}
counters=tree.find('.//t:ResultSummary/t:Counters',ns).attrib
assert int(counters['total'])==int(counters['executed'])==count
assert int(counters['passed'])==counts['Passed'] and int(counters['failed'])==counts['Failed'] and counts['NotExecuted']==0
assert terminal['exitCode']==(1 if counts['Failed'] else 0)
binding=read(run/'loaded-native.json');assert len(binding['native'])==62
assert {r['name']:r['sha256'] for r in binding['native']}=={r['name']:r['sha256'] for r in build['native62']}
for row in binding['native']:
    assert Path(row['path']).parent.resolve()==(runtime/'occt').resolve()
    assert sha(Path(row['path']))==row['sha256']
failures=[{'name':r.attrib['testName'],'message':r.findtext('t:Output/t:ErrorInfo/t:Message','',ns),
    'stack':r.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns)} for r in rows if r.attrib['outcome']=='Failed']
out=draft/f'{a.group}-{a.configuration.lower()}-{a.attempt}-verification.json';assert not out.exists()
out.write_text(json.dumps({'recordedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'state':'ACTUAL_UNCHANGED_SCOPE_RUNTIME_AND_LOADED_NATIVE62_AUDITED','configuration':a.configuration,
    'group':a.group,'attempt':a.attempt,'count':count,'passed':counts['Passed'],'failed':counts['Failed'],'skipped':0,
    'names':expected,'failures':failures,'build':ref(build_path),'terminal':ref(run/'exit.json'),
    'trx':ref(run/'runtime.trx'),'loadedNative':ref(run/'loaded-native.json'),
    'sdkVersion':build.get('sdkVersion','SDK31 exact-step candidate; see build payload'),
    'allOriginalNamesAndAssertionsUnchanged':True,'originalBudgetsChanged':False,
    'performanceAcceptance':False,'productionApplied':False,'cleanup':False,'desktopInputStarted':False,
    'AF':'ACTIVE','G':'USER_DEFERRED'},indent=2)+'\n',encoding='utf-8')
print(json.dumps({'configuration':a.configuration,'group':a.group,'passed':counts['Passed'],'failed':counts['Failed'],'native62':True}))
