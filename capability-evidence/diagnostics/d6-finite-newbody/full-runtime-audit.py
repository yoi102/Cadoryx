from pathlib import Path
import argparse,datetime,hashlib,json,xml.etree.ElementTree as ET
parser=argparse.ArgumentParser();parser.add_argument('--draft',required=True);parser.add_argument('--configuration',choices=['Release','Debug'],required=True);parser.add_argument('--group',choices=['new56','finite92','finite99','agent189','full6619'],required=True);args=parser.parse_args()
cad=Path('C:/Users/yoiri/source/repos/Cadoryx');draft=cad/'artifacts'/args.draft;buildname='build-both-3-verification.json' if args.draft.endswith('draft-1') else 'build-both-1-verification.json'
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'));sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper();ref=lambda p:{'path':str(p),'sha256':sha(p)}
build=read(draft/buildname);cfg=build['configurations'][args.configuration];source=Path(build['workspace']);runtime=Path(cfg['testDirectory'])
for row in build['sourcePayload']:assert sha(source/row['path'])==row['sha256']
for row in cfg['testPayload']:assert sha(runtime/row['path'])==row['sha256']
if args.group=='full6619':expected=cfg['inventoryNames']
elif args.group=='new56':expected=cfg['newNames']
elif args.group in ('finite92','finite99'):expected=cfg['relatedNames']
else:
 classes=('AgentIdentityFieldPresentationTests','ToolPresentationTests','AgentSuccessfulActionReceiptTests','AgentEmptyResponseRecoveryTests','DrawingInspectionTypeFeedbackTests','DrawingContractRequestGuideTests','DrawingAgentTests','DrawingSourcePathFeedbackTests','DrawingSourcePathDispatchTests')
 expected=[n for n in cfg['inventoryNames'] if any('.'+c+'.' in n.split('(')[0] for c in classes)]
assert len(expected)=={'new56':56,'finite92':92,'finite99':99,'agent189':189,'full6619':6619}[args.group]
run=draft/(args.group+'-'+args.configuration.lower()+'-1');terminal=read(run/'exit.json');ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'};tree=ET.parse(run/'runtime.trx');rows=tree.findall('.//t:UnitTestResult',ns)
assert len(rows)==len(expected)==len(set(r.attrib['testName'] for r in rows)) and set(r.attrib['testName'] for r in rows)==set(expected)
counts={s:sum(r.attrib['outcome']==s for r in rows) for s in ('Passed','Failed','NotExecuted')};counters=tree.find('.//t:ResultSummary/t:Counters',ns).attrib
assert int(counters['total'])==int(counters['executed'])==len(rows) and int(counters['passed'])==counts['Passed'] and int(counters['failed'])==counts['Failed'] and counts['NotExecuted']==0
assert terminal['exitCode']==(1 if counts['Failed'] else 0)
binding=read(run/'loaded-native.json');assert len(binding['native'])==62
assert {r['name']:r['sha256'] for r in binding['native']}=={r['name']:r['sha256'] for r in build['native62']}
for row in binding['native']:assert Path(row['path']).parent.resolve()==(runtime/'occt').resolve() and sha(Path(row['path']))==row['sha256']
failures=[{'name':r.attrib['testName'],'message':r.findtext('t:Output/t:ErrorInfo/t:Message','',ns),'stack':r.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns)} for r in rows if r.attrib['outcome']=='Failed']
out=draft/(args.group+'-'+args.configuration.lower()+'-1-verification.json');assert not out.exists()
out.write_text(json.dumps({'recordedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),'state':'FINAL_D6_ORIGINAL_FULL_SCOPE_ACTUALLY_RUN_AND_AUDITED','configuration':args.configuration,'group':args.group,'count':len(rows),'passed':counts['Passed'],'failed':counts['Failed'],'skipped':0,'names':expected,'failures':failures,'build':ref(draft/buildname),'terminal':ref(run/'exit.json'),'trx':ref(run/'runtime.trx'),'loadedNative':ref(run/'loaded-native.json'),'actualSdk31Native62':True,'allOriginalNamesAndAssertionsUnchanged':True,'performanceAcceptance':False,'productionApplied':False,'cleanup':False,'desktopInputStarted':False,'AF':'ACTIVE','G':'USER_DEFERRED'},indent=2)+'\n',encoding='utf-8')
print(json.dumps({'configuration':args.configuration,'group':args.group,'passed':counts['Passed'],'failed':counts['Failed'],'actualNative62':True,'performanceAcceptance':False}))
