from pathlib import Path
import datetime, hashlib, json

root=Path('C:/Users/yoiri/source/repos/Cadoryx')
task=root/'artifacts/d6-window-host-5'
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref=lambda p:{'path':str(p),'sha256':sha(p)}
build_path=root/'artifacts/d6-finite-newbody-draft-4/build-both-1-verification.json'
build=read(build_path);workspace=Path(build['workspace'])
for row in build['sourcePayload']:assert sha(workspace/row['path'])==row['sha256']
assert len(build['sourcePayload'])==1073
for f in ('WindowHost.cs','WindowHost.csproj'):
 assert sha(task/f)==sha(root/'capability-evidence/diagnostics/d6-window-acceptance'/f)
text=(task/'WindowHost.cs').read_text(encoding='utf-8-sig')
assert 'DispatcherPriority.Normal).Task.Unwrap()' in text and 'await productionOpen;' in text
assert 'document.EditFeature(feature.Id)' in text and 'watch.Elapsed.TotalSeconds<300' in text
assert 'readiness.Elapsed.TotalSeconds<30' in text
assert 'PreviewCommand.Execute' not in text and 'ConfirmCommand.Execute' not in text and 'LoadFacesCommand.Execute' not in text
assert 'SendInput' not in text and 'System.Windows.Automation' not in text
configs={}
for config in ('Release','Debug'):
 label=config.lower();terminal=task/f'build-{label}-1-exit.json';assert read(terminal)['exitCode']==0
 log=task/f'build-{label}-1.log';assert '0 Warning(s)' in log.read_text(encoding='utf-8-sig') and '0 Error(s)' in log.read_text(encoding='utf-8-sig')
 assert (task/f'build-{label}-1-stderr.log').stat().st_size==0
 app=workspace/f'Cadoryx.wpf/bin/{config}/net10.0-windows/win-x64'
 payload=task/f'bin/{config}/net10.0-windows/win-x64'
 copied=[]
 for p in payload.rglob('*.dll'):
  if p.name=='WindowHost.dll':continue
  rel=p.relative_to(payload);assert sha(p)==sha(app/rel),str(rel)
  copied.append({'path':rel.as_posix(),'sha256':sha(p)})
 for row in build['sdk13']:assert sha(payload/row['name'])==row['sha256']
 for row in build['native62']:assert sha(payload/'occt'/row['name'])==row['sha256']
 for row in build['configurations'][config]['testPayload']:
  if row['path'].startswith('Cadoryx.') and '/' not in row['path'] and row['path'].endswith('.dll') and (app/row['path']).exists():
   assert sha(app/row['path'])==row['sha256']
 app_payload=[{'path':p.relative_to(app).as_posix(),'sha256':sha(p)} for p in sorted(app.rglob('*')) if p.is_file()]
 configs[config]={'build':ref(terminal),'buildLog':ref(log),'candidateAppDirectory':str(app),'candidateAppPayload':app_payload,
  'observerDirectory':str(payload),'copiedCandidateDlls':copied,'observerDll':ref(payload/'WindowHost.dll'),'observerExe':ref(payload/'WindowHost.exe'),
  'actualWindowsStarted':False,'debugUsesPackageReleaseNative':True if config=='Debug' else False}
first=root/'artifacts/d6-window-host-1';assert read(first/'build-release-1-exit.json')['exitCode']==1
assert "name 'Strings'" in (first/'build-release-1.log').read_text(encoding='utf-8-sig')
fixtures_path=root/'capability-evidence/AF_PENDING_D6_WINDOW_FIXTURES_VERIFICATION.json'
fixtures=read(fixtures_path);matrix=[]
for phase in ('create','edit'):
 for language in ('en-US','zh-CN','ja-JP'):
  for row in fixtures['archives']:
   fixture=row['fixture'];kind=fixture['kind']
   # Every kind/direction is exercised in every language; each exact fixture
   # gets all three close routes across the language matrix.
   route=('confirm','cancel','close')[(('en-US','zh-CN','ja-JP').index(language)+('plane','sphere','cylinder').index(kind))%3]
   output=task/(f'window-{phase}-{language}-{fixture["label"]}-1')
   assert not output.exists()
   matrix.append({'phase':phase,'language':language,'label':fixture['label'],'route':route,'fixture':{'path':fixture['path'],'sha256':fixture['sha256']},
    'arguments':['--d6-newbody-ui-smoke',str(output),fixture['path'],language,route,phase],'output':str(output),'state':'NOT_RUN'})
assert len(matrix)==36
out=root/'capability-evidence/AF_PENDING_D6_WINDOW_OBSERVER_V3_BUILD_VERIFICATION.json';assert not out.exists()
out.write_text(json.dumps({'recordedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),
 'state':'PASS_BOTH_COMPILED_PRODUCTION_WINDOW_OBSERVER_SOURCE1073_COPIED_PAYLOAD_NATIVE62_DISK_ACTUAL_WINDOWS_NOT_RUN',
 'candidateBuild':ref(build_path),'sourceCount':1073,'source':[ref(task/f) for f in ('WindowHost.cs','WindowHost.csproj')],
 'priorEditOnlyObserverBuild':ref(root/'capability-evidence/AF_PENDING_D6_WINDOW_OBSERVER_BUILD_VERIFICATION.json'),
 'creationObserverFirstCompileFailure':ref(root/'artifacts/d6-window-host-3/build-release-1.log'),
 'creationObserverFirstSource':ref(root/'artifacts/d6-window-host-3/WindowHost.cs'),
 'modalSequencingCorrection':'Production open is queued before the observer readiness await; observe during ShowDialog and await production completion before document checks. Original36 entries,6 trace states and30/300s limits retained.',
 'priorFrozenObserverWithSynchronousOpen':ref(root/'artifacts/d6-window-host-4/WindowHost.cs'),
 'creationCompileCorrection':'Added the existing Cadoryx.Editor namespace for SketchDraft; all original observer assertions and36 matrix entries retained.',
 'configurations':configs,'fixtures':ref(fixtures_path),'uiMatrix':matrix,'requiredTrace':[
  'INVENTORY_CLEARED_EXPLICIT_SELECTION','MISSING_LIMIT_PREVIEW_REJECTED','NEAR_PREVIEW_READY',
  'BRANCH_INVALIDATED_READY_PREVIEW','FAR_PREVIEW_REJECTED','NEAR_PREVIEW_RESTORED'],
 'readinessSeconds':30,'perWindowSeconds':300,'originalNativeAndToolBudgetsChanged':False,
 'observerInjectsDesktopInput':False,'externalComputerUseAndDesktopReviewRequired':True,
 'firstCompileFailure':ref(first/'build-release-1.log'),'firstCompileSource':ref(first/'WindowHost.cs'),
 'firstCompileCorrection':'Added the existing strong-resource namespace alias; bounded the new unrun per-window observer to300s. Product and original test payloads unchanged.',
 'full6619TerminalRequiredBeforeWindowStart':True,'desktopStartNotificationRequired':True,
 'actualWindowsStarted':False,'actualWindowsPassed':False,'nativeRuntimeBindingCaptured':False,
 'productionApplied':False,'cleanup':False,'AF':'ACTIVE','G':'USER_DEFERRED'},indent=2)+'\n',encoding='utf-8')
print('PASS both observer builds and final app/core/sdk13/native62 disk copies; thirty-six creation/edit UI matrix entries prepared, zero actual windows started.')
