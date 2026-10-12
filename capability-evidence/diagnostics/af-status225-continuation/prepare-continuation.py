from pathlib import Path
import datetime, hashlib, json, subprocess

root=Path('C:/Users/yoiri/source/repos/Cadoryx')
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref=lambda p:{'path':str(p),'sha256':sha(p)}
now=lambda:datetime.datetime.now(datetime.timezone.utc).isoformat()
def write(p,data):
    assert not p.exists(),str(p)
    p.write_text(json.dumps(data,indent=2)+'\n',encoding='utf-8')

# Observe the lost coordinator state; do not infer why it ended or overwrite
# the empty first runtime directory. Recovery gets a separate attempt.
pwsh='C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/native/powershell/pwsh.exe'
command="Get-CimInstance Win32_Process | Where-Object { $_.Name -match 'pwsh|testhost|Cadoryx|OcctSharp|dotnet' } | Select-Object ProcessId,Name,CommandLine | ConvertTo-Json -Depth 3"
observed=subprocess.run([pwsh,'-NoProfile','-Command',command],capture_output=True,text=True,check=True)
processes=json.loads(observed.stdout)
if isinstance(processes,dict):processes=[processes]
for p in processes:
    if p['Name']=='pwsh.exe' and p.get('CommandLine'):
        assert not any(' -File '+x in p['CommandLine'] for x in (
            'artifacts/run-b18-solver21-full-after-provider-1.ps1',
            'artifacts/run-d6-full6619-after-solver21-1.ps1',
            'artifacts/run-d6-window-host-build-after-reservation-4.ps1'))
partial=root/'artifacts/b18-prescribed-complete-authors-draft-2/full6556-release-1'
assert partial.exists() and not list(partial.iterdir())
assert not (partial.parent/'full-both-1-exit.json').exists()
assert not (root/'artifacts/d6-finite-newbody-draft-4/full-both-1-exit.json').exists()
queue=root/'capability-evidence/diagnostics/af-status225-continuation'
queue.mkdir(exist_ok=False)
write(queue/'coordinator-observation.json',{'recordedAt':now(),
    'state':'ORIGINAL_COORDINATORS_ABSENT_WITHOUT_TERMINALS_OR_RUNTIME_OUTPUT',
    'processes':processes,'emptyFirstSolverReleaseDirectory':str(partial),
    'cause':'UNKNOWN','noRunningJobStopped':True,'originalEvidencePreserved':True,
    'recoveryRequiresFreshAttempt':True,'cleanup':False,'AF':'ACTIVE'})
reservation=root/'artifacts/native-lane-reservation-preview83-1.json'
write(root/'artifacts/native-lane-reservation-preview83-1-release.json',{
    'recordedAt':now(),'requesterThread':read(reservation)['requesterThread'],
    'reservation':ref(reservation),'basis':'Received requester message: production OpenPathAsync exited; scheduling lane released; no further quiet performance reservation.',
    'originalConsumersStopped':False,'otherThreadResultsUsedForCadoryxAcceptance':False,'cleanup':False})

# Freeze revision 5 separately; preserve revision 4 bytes and original queue.
audit=root/'capability-evidence/diagnostics/d6-window-acceptance/build-audit.py'
audit_text=audit.read_text(encoding='utf-8-sig')
audit_text=audit_text.replace("task=root/'artifacts/d6-window-host-4'","task=root/'artifacts/d6-window-host-5'")
audit_text=audit_text.replace('AF_PENDING_D6_WINDOW_OBSERVER_V2_BUILD_VERIFICATION.json','AF_PENDING_D6_WINDOW_OBSERVER_V3_BUILD_VERIFICATION.json')
audit_text=audit_text.replace("assert 'document.EditFeature(feature.Id)' in text", "assert 'DispatcherPriority.Normal).Task.Unwrap()' in text and 'await productionOpen;' in text\nassert 'document.EditFeature(feature.Id)' in text")
audit_text=audit_text.replace("'creationCompileCorrection':", "'modalSequencingCorrection':'Production open is queued before the observer readiness await; observe during ShowDialog and await production completion before document checks. Original36 entries,6 trace states and30/300s limits retained.',\n 'priorFrozenObserverWithSynchronousOpen':ref(root/'artifacts/d6-window-host-4/WindowHost.cs'),\n 'creationCompileCorrection':")
audit.write_text(audit_text,encoding='utf-8')
task=root/'artifacts/d6-window-host-5';task.mkdir(exist_ok=False)
for name in ('WindowHost.cs','WindowHost.csproj','build-audit.py'):
    (task/name).write_bytes((audit.parent/name).read_bytes())
runner=root/'artifacts/run-d6-window-host-build-after-reservation-5.ps1'
runner.write_text((root/'artifacts/run-d6-window-host-build-after-reservation-4.ps1').read_text(encoding='utf-8-sig').replace('d6-window-host-4','d6-window-host-5'),encoding='utf-8')
write(task/'preparation-1.json',{'recordedAt':now(),
    'state':'REVISION5_QUEUED_MODAL_OPEN_OBSERVER_SOURCE_PREPARED_NOT_COMPILED',
    'source':[ref(task/f) for f in ('WindowHost.cs','WindowHost.csproj','build-audit.py')],
    'runner':ref(runner),'priorFrozenRevision4':ref(root/'artifacts/d6-window-host-4/preparation-1.json'),
    'candidateSourceCount':1073,'matrixCount':36,'configurations':['Release','Debug'],
    'actualWindowsStarted':False,'compilationStarted':False,'cleanup':False,'AF':'ACTIVE','G':'USER_DEFERRED'})
print('PASS preserved absent original coordinator evidence; released reservation; froze modal observer revision5.')
