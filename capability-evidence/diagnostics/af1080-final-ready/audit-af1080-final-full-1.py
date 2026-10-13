from pathlib import Path
from datetime import datetime,timezone
import json,hashlib,shutil,xml.etree.ElementTree as ET
root=Path(__file__).resolve().parents[1];draft=root/'artifacts/af1080-final-1'
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref=lambda p:{'path':p.relative_to(root).as_posix(),'sha256':sha(p)}
freeze=read(draft/'freeze.json');suite=read(draft/'full-both-exit.json')
parent=read(root/'capability-evidence/diagnostics/af1076-root-full/verification.json')
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
for row in freeze['sourcePayload']:assert sha(Path(freeze['workspace'])/row['path'])==row['sha256']
expected={r['name']:r['sha256'] for r in freeze['native62']};assert len(expected)==62
dest=root/'capability-evidence/diagnostics/af1080-final-full';dest.mkdir(parents=True,exist_ok=True)
checks=[];namesets=[]
for gate in suite['configurations']:
    config=gate['configuration'];folder=Path(gate['output']);terminal=read(folder/'exit.json')
    cfg=next(r for r in freeze['configurations'] if r['configuration']==config);binary=Path(cfg['testDirectory'])
    for row in cfg['payload']:assert sha(binary/row['path'])==row['sha256']
    trx=folder/'runtime.trx';tree=ET.parse(trx);counts=tree.find('.//t:Counters',ns).attrib
    results=tree.findall('.//t:UnitTestResult',ns);names={r.attrib['testName'] for r in results}
    assert len(results)==len(names)==int(counts['total'])==6815
    old=next(r for r in parent['configurations'] if r['configuration']==config)
    old_trx=Path(old['trx']['path']);assert sha(old_trx)==old['trx']['sha256']
    old_results=ET.parse(old_trx).findall('.//t:UnitTestResult',ns);old_names={r.attrib['testName'] for r in old_results};assert len(old_names)==6652 and old_names<=names
    old_failures={r.attrib['testName'] for r in old_results if r.attrib['outcome']=='Failed'}
    failures=[{'name':r.attrib['testName'],'error':r.find('.//t:Message',ns).text if r.find('.//t:Message',ns) is not None else None} for r in results if r.attrib['outcome']=='Failed']
    failed_names={r['name'] for r in failures}
    assert terminal['exitCode']==gate['exitCode']==(1 if failures else 0)
    assert int(counts['passed'])+int(counts['failed'])+int(counts['notExecuted'])==6815
    binding=read(folder/'loaded-native.json');assert len(binding['native'])==62 and {r['name'] for r in binding['native']}==expected.keys()
    for row in binding['native']:assert Path(row['path']).parent==binary/'occt' and row['sha256']==expected[row['name']]==sha(Path(row['path']))
    for file in ['runtime.trx','loaded-native.json','exit.json','stdout.log','stderr.log']:shutil.copyfile(folder/file,dest/f'{config.lower()}-{file}')
    checks.append({'configuration':config,'total':6815,'passed':int(counts['passed']),'failed':int(counts['failed']),'skipped':int(counts['notExecuted']),
                   'parent6652NamesRetained':True,'added163Names':sorted(names-old_names),'failures':failures,
                   'newFailureNames':sorted(failed_names-old_failures),'originalFailuresNowPassed':sorted(old_failures-failed_names),
                   'actualNative62Matched':True,'trx':ref(dest/f'{config.lower()}-runtime.trx'),'binding':ref(dest/f'{config.lower()}-loaded-native.json')})
    namesets.append(names)
assert len(checks)==2 and namesets[0]==namesets[1]
record={'recordedAt':datetime.now(timezone.utc).isoformat(),'state':'AF1080_FROZEN_FULL6815_BOTH_ACTUALLY_EXECUTED_AND_VERIFIED',
        'freeze':ref(draft/'freeze.json'),'suite':ref(draft/'full-both-exit.json'),'baseHead':freeze['baseHead'],'configurations':checks,
        'allPassed':all(r['failed']==r['skipped']==0 for r in checks),'originalStrictSTEPGateChanged':False,
        'desktopOrModelGateProvenByFullRegression':False,'AFComplete':False,'cleanup':False,'G':'USER_DEFERRED'}
path=dest/'verification.json';assert not path.exists();path.write_text(json.dumps(record,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'configs':[{'name':c['configuration'],'passed':c['passed'],'failed':c['failed'],'skipped':c['skipped'],'newFailures':len(c['newFailureNames'])} for c in checks]}))
