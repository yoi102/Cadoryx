$ErrorActionPreference='Stop'
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx'
$taskDraft=Join-Path $taskRoot 'artifacts/af1081-local86-1'
$taskAudit=Join-Path $taskRoot 'capability-evidence/diagnostics/af1081-local86/full-verification.json'
$taskState=Join-Path $taskDraft 'provider-queue-state.json'
if(Test-Path $taskState){throw 'Queue already exists; inspect actual process'}
@{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');coordinatorPid=$PID;state='WAITING_FOR_FULL_BOTH_TERMINAL_AUDIT';nativeStarted=$false;desktopInputQueued=$false}|ConvertTo-Json|Set-Content $taskState
while(!(Test-Path $taskAudit)) {
 if(Test-Path (Join-Path $taskDraft 'incomplete.json')){throw 'Full run incomplete; preserve outputs'}
 if(Test-Path (Join-Path $taskDraft 'full-both-exit.json')){
  Start-Sleep -Seconds 5
  if(!(Test-Path $taskAudit)){throw 'Full terminal exists but independent audit did not pass'}
  break
 }
 $taskStart=Get-Content (Join-Path $taskDraft 'full6830-release-1/start.json') -Raw|ConvertFrom-Json
 $taskCoordinator=Get-Process -Id $taskStart.coordinatorPid -ErrorAction SilentlyContinue
 if(!$taskCoordinator){throw 'Full coordinator exited without required terminal audit'}
 Start-Sleep -Seconds 5
}
$taskProof=Get-Content $taskAudit -Raw|ConvertFrom-Json
if($taskProof.configurations.Count -ne 2 -or @($taskProof.configurations|Where-Object {$_.total -ne 6830 -or $_.failed -ne 24 -or $_.skipped -ne 0 -or $_.newFailureNames.Count -ne 0 -or !$_.actualNative62Matched}).Count -ne 0){throw 'Inspect changed full scope or failures before model run'}
@{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');coordinatorPid=$PID;state='STARTING_ORIGINAL_PROVIDER_SCENARIOS';desktopInputQueued=$false}|ConvertTo-Json|Set-Content $taskState
& (Join-Path $taskRoot 'capability-evidence/diagnostics/af1081-local86/run-provider.ps1')
@{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');coordinatorPid=$PID;state='ORIGINAL_PROVIDER_SCENARIOS_TERMINAL';desktopInputQueued=$false;productionApplied=$false;publiclyPublished=$false;AFComplete=$false}|ConvertTo-Json|Set-Content $taskState
