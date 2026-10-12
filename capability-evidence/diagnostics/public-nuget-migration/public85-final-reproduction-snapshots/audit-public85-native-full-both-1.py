from pathlib import Path
import collections, datetime, hashlib, json, xml.etree.ElementTree as ET
root=Path(__file__).resolve().parents[1]
out=root/'artifacts/public85-runtime-acceptance-1'
r=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref=lambda p:{'path':str(p),'sha256':sha(p)}
freeze=r(out/'freeze.json');terminal=r(out/'full-both-exit.json')
assert terminal['version']=='8.0.1-preview.85' and len(terminal['gates'])==2
assert sha(Path(freeze['consumerProof']['path']))==freeze['consumerProof']['sha256']
for row in freeze['sourcePayload']:
 assert sha(Path(freeze['workspace'])/row['path'])==row['sha256'],row['path']
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
old=root/'artifacts/b18-prescribed-complete-authors-draft-2/full6556-release-2/runtime.trx'
old_results=ET.parse(old).findall('.//t:UnitTestResult',ns)
old_names=collections.Counter(x.attrib['testName'] for x in old_results)
assert sum(old_names.values())==6556
old_failures={x.attrib['testName'] for x in old_results if x.attrib['outcome']=='Failed'}
checks=[]
for cfg in ['Release','Debug']:
 gate=next(g for g in terminal['gates'] if g['configuration']==cfg)
 path=Path(gate['terminal']);assert sha(path)==gate['sha256']
 g=r(path);d=path.parent;trx=d/'runtime.trx';x=ET.parse(trx)
 counts={k:int(v) for k,v in x.find('.//t:Counters',ns).attrib.items()}
 assert counts['total']==counts['executed']==freeze['expectedFullTests']==6619
 assert counts['notExecuted']==0 and counts['passed']+counts['failed']==6619
 assert g['exitCode']==gate['exitCode']==(1 if counts['failed'] else 0)
 results=x.findall('.//t:UnitTestResult',ns)
 names=collections.Counter(n.attrib['testName'] for n in results)
 assert sum(names.values())==6619 and not old_names-names
 added=list((names-old_names).elements());assert len(added)==63
 native_path=d/'loaded-native.json';native=r(native_path)['native']
 cfgproof=next(c for c in freeze['configurations'] if c['configuration']==cfg)
 expected={n['name']:n['sha256'] for n in freeze['native62']}
 assert len(native)==62 and {n['name'] for n in native}==expected.keys()
 for n in native:
  assert Path(n['path']).parent==Path(cfgproof['testDirectory'])/'occt'
  assert n['sha256']==expected[n['name']]==sha(Path(n['path']))
 for f in cfgproof['payload']:
  if f['path'].endswith(('.dll','.exe','.deps.json','.runtimeconfig.json')):
   assert sha(Path(cfgproof['testDirectory'])/f['path'])==f['sha256']
 failed=[]
 for n in results:
  if n.attrib['outcome']=='Failed':
   msg=n.find('t:Output/t:ErrorInfo/t:Message',ns)
   failed.append({'name':n.attrib['testName'],'message':msg.text if msg is not None else ''})
 checks.append({'configuration':cfg,'total':6619,'passed':counts['passed'],'failed':counts['failed'],'skipped':0,'original6556NamesRetained':True,'added63Names':sorted(added),'failureDetails':sorted(failed,key=lambda x:x['name']),'newFailureNamesComparedWithOriginal6556':sorted({f['name'] for f in failed}-old_failures),'originalFailureNamesNowPassed':sorted(old_failures-{f['name'] for f in failed}),'terminal':ref(path),'trx':ref(trx),'loadedNative':ref(native_path),'native62ActualPathsAndHashesMatchPublicPayload':True})
report=out/'full-both-verification.json';assert not report.exists()
result={'recordedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),'state':'PUBLIC85_FINAL1073_FULL6619_BOTH_EXECUTED_WITH_FAILURES' if any(c['failed'] for c in checks) else 'PASS_PUBLIC85_FINAL1073_FULL6619_BOTH','version':'8.0.1-preview.85','freeze':ref(out/'freeze.json'),'consumerProof':freeze['consumerProof'],'terminal':ref(out/'full-both-exit.json'),'configurations':checks,'originalAssertionsTolerancesAndBudgetsChanged':False,'source1073AppliedToProduction':False,'desktopPerformanceAndExternalProviderAccepted':False,'cleanup':False,'AF':'ACTIVE','G':'USER_DEFERRED'}
report.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
import shutil
permanent=root/'capability-evidence/diagnostics/public-nuget-migration/public85-final1073-full-both-verification.json'
assert not permanent.exists();shutil.copy2(report,permanent)
print(result['state'],[(c['configuration'],c['passed'],c['failed']) for c in checks])
