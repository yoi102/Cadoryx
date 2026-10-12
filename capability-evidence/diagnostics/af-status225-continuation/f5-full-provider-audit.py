from pathlib import Path
import datetime,hashlib,json
root=Path('C:/Users/yoiri/source/repos/Cadoryx');task=root/'artifacts/f5-drawing-source-path-feedback-draft-2'
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'));sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper();ref=lambda p:{'path':str(p),'sha256':sha(p)}
build_path=task/'provider-build-1-verification.json';build=read(build_path)
assert build['contextLength']==8192 and build['maxToolRounds']==16 and build['originalCSharpPromptsAndAssertionsByteIdentical']
for row in build['source']:
 for item in (row['original'],row['prepared']):assert sha(Path(item['path']))==item['sha256']
 if Path(row['original']['path']).suffix=='.cs':assert row['original']['sha256']==row['prepared']['sha256']
full=[]
for config in ('Release','Debug'):
 p=task/f'full6556-{config.lower()}-1-verification.json';j=read(p)
 assert j['count']==6556 and j['passed']==6432 and j['failed']==124 and j['skipped']==0 and j['actualSdk31Native62']
 base=read(root/f'artifacts/af-sdk31-application-draft-1/related986-{config.lower()}-1-verification.json')
 agent=read(task/f'agent189-{config.lower()}-1-verification.json')
 assert {f['name'] for f in j['failures']}=={f['name'] for f in base['failures']}|{f['name'] for f in agent['failures']}
 full.append({'configuration':config,'passed':6432,'failed':124,'skipped':0,'audit':ref(p),'allFailuresPreviouslyIdentified':True})
suite_path=task/'provider-both-1-exit.json';suite=read(suite_path);rows=[]
expected={r['name']:r['sha256'] for r in build['native62']}
for scenario in ('drawing','workspace'):
 terminal_path=task/f'lmstudio-{scenario}-1-exit.json';terminal=read(terminal_path)
 original=next(r for r in suite['scenarios'] if r['scenario']==scenario);assert original['sha256']==sha(terminal_path)
 assert terminal['contextLength']==8192 and terminal['maxToolRounds']==16 and terminal['model']==build['model'] and terminal['endpoint']==build['endpoint']
 assert terminal['exitCode']==(1 if scenario=='drawing' else 0)
 result_path=task/f'lmstudio-{scenario}-1/result.json';result=read(result_path)
 assert result['passed']==(scenario=='workspace') and result['actualProductionTools'] and result['assetsAfterClose']==0
 binding_path=task/f'lmstudio-{scenario}-1-loaded-native.json';binding=read(binding_path)
 assert len(binding['native'])==62 and {r['name']:r['sha256'] for r in binding['native']}==expected
 for module in binding['native']:assert Path(module['path']).parent.resolve()==(Path(build['candidate'])/'occt').resolve()
 calls_path=task/f'lmstudio-{scenario}-1/tool-calls.json';calls=read(calls_path)
 info={'scenario':scenario,'passed':result['passed'],'terminal':ref(terminal_path),'result':ref(result_path),'actualNative':ref(binding_path),
  'toolCalls':ref(calls_path),'turns':result['turns'],'assetsAfterClose':0,'failure':result['failure'],'actualSdk31Native62':True}
 if scenario=='workspace':
  assert [r['turn'] for r in result['turns']]==list(range(1,9))
  saved=task/'lmstudio-workspace-1/workspace-provider-saved.cadoryx';assert sha(saved)==result['savedSha256'].upper()
  info['saved']=ref(saved)
 else:
  assert 'successful production receipt' in result['failure']
  draw=[{'arguments':json.loads(r['ArgumentsJson']),'result':json.loads(r['result'])} for r in calls if r['Name']=='cad_drawing']
  assert any(r['arguments'].get('type')=='sheet' and r['result'].get('success') and r['result'].get('changed') for r in draw)
  assert any(r['arguments'].get('type')=='view' and r['result'].get('success') and r['result'].get('changed') for r in draw)
  assert any(r['arguments'].get('action')=='inspect_sources' and r['result'].get('success') for r in draw)
  assert not any(r['arguments'].get('type')=='dimension' and r['result'].get('success') and r['result'].get('changed') for r in draw)
  response_path=task/'lmstudio-drawing-1/response-018.json';reply=read(response_path)
  assert reply['Content']=='assistant\n<think>\n\n</think>\n\n' and reply['ToolCalls']==[]
  request_path=task/'lmstudio-drawing-1/request-018.json';request=read(request_path)
  assert request['MaxOutputTokens']==1024
  info.update({'drawingActions':draw,'finalReply':ref(response_path),'finalRequest':ref(request_path),
   'controlOnlyReplyRetained':True,'finalRecoverySelectedToolCount':len(request['Tools']),
   'sheetAndViewSucceededDimensionStillMissing':True})
 rows.append(info)
out=root/'capability-evidence/AF_PENDING_F5_SOURCE1070_FULL_PROVIDER_VERIFICATION.json';assert not out.exists()
out.write_text(json.dumps({'recordedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),
 'state':'ORIGINAL_SOURCE1070_FULL6556_BOTH_AND_BONSAI_BOTH_ACTUALLY_RUN_FAILURES_RETAINED',
 'build':ref(build_path),'providerSuite':ref(suite_path),'full6556Both':full,'provider':rows,
 'contextLength':8192,'maxToolRounds':16,'model':build['model'],'originalCSharpPromptsAndAssertionsPreserved':True,
 'nativeHashCaptureMatchesFrozen62':True,'nativeDllHashesRecomputedDuringAggregateAudit':False,
 'finalSource1073Acceptance':False,'physicalPointerVerified':False,'performanceAcceptance':False,
 'productionApplied':False,'cleanup':False,'AF':'ACTIVE','G':'USER_DEFERRED'},indent=2)+'\n',encoding='utf-8')
print('PASS original full6556 scope audit:both6432/124; original Bonsai workspace8 PASS, drawing dimension missing/control-only FAIL. Final1073 gates separate.')
