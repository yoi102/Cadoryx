[CmdletBinding()]
param(
 [Parameter(Mandatory)][int]$FullCoordinatorPid,
 [Parameter(Mandatory)][string]$FullCoordinatorStartUtc
)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskDraft=Join-Path $taskRoot 'artifacts/af1082-f5-d7-local87cu1-1'
$taskQueue=Join-Path $taskDraft 'followup-queue.json'
$taskTerminal=Join-Path $taskDraft 'followup-queue-exit.json'
if((Test-Path $taskQueue)-or(Test-Path $taskTerminal)){throw 'Current followup queue exists; preserve its coordinator'}
$taskExpectedStart=[DateTimeOffset]::Parse($FullCoordinatorStartUtc)
$taskParent=Get-Process -Id $FullCoordinatorPid -ErrorAction Stop
if($taskParent.StartTime.ToUniversalTime().Ticks -ne $taskExpectedStart.UtcDateTime.Ticks){throw 'Full coordinator identity changed'}
$taskStart=Get-Content (Join-Path $taskDraft 'full7117-release-1/start.json') -Raw|ConvertFrom-Json -DateKind String
if($taskStart.coordinatorPid -ne $FullCoordinatorPid -or $taskStart.scope -ne 7117){throw 'Full coordinator does not own this frozen scope'}
$taskEntry=[ordered]@{startedUtc=[DateTimeOffset]::UtcNow.ToString('o');coordinatorPid=$PID;coordinatorStartUtc=(Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('o');fullCoordinatorPid=$FullCoordinatorPid;fullCoordinatorStartUtc=$FullCoordinatorStartUtc;state='WAITING_CURRENT_FULL_BOTH7117';plannedStages=@('recovery24','main-status36','provider');desktopInputInjected=$false;AFComplete=$false;G='USER_DEFERRED';cleanup=$false}
$taskEntry|ConvertTo-Json -Depth 6|Set-Content $taskQueue
Write-Output 'Followups queued behind this exact full coordinator; no native consumer started.'
$taskStages=@()
try {
 while($true){
  $taskRunning=Get-Process -Id $FullCoordinatorPid -ErrorAction SilentlyContinue
  if(!$taskRunning){break}
  if($taskRunning.StartTime.ToUniversalTime().Ticks -ne $taskExpectedStart.UtcDateTime.Ticks){throw 'Full coordinator PID reused; no followups allowed'}
  Start-Sleep -Milliseconds 500
 }
 . (Join-Path $PSScriptRoot 'require-current-return.ps1')
 Assert-CurrentSdkNativeReturn -TaskRoot $taskRoot
 & 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' -X utf8 (Join-Path $PSScriptRoot 'require-full-gate.py')
 if($LASTEXITCODE -ne 0){throw 'Complete current full gate required; no partial or stale result accepted'}
 foreach($taskStage in @('recovery24','main-status36')){
  $taskEntry.state='RUNNING_'+$taskStage
  $taskEntry|ConvertTo-Json -Depth 6|Set-Content $taskQueue
  & (Join-Path $PSScriptRoot 'run-recovery.ps1') -Label $taskStage
  $taskAuditor=if($taskStage -ceq 'recovery24'){'audit-recovery24.py'}else{'audit-main-status36.py'}
  & 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' -X utf8 (Join-Path $PSScriptRoot $taskAuditor)
  if($LASTEXITCODE -ne 0){throw ('Independent recovery audit failed: '+$taskStage)}
  $taskStages+=@{stage=$taskStage;runtimeAndAuditCompleted=$true;wholeImageReview='PENDING_DIRECT_REVIEW'}
 }
 $taskEntry.state='RUNNING_ORIGINAL_PROVIDER'
 $taskEntry|ConvertTo-Json -Depth 6|Set-Content $taskQueue
 & (Join-Path $PSScriptRoot 'run-provider.ps1')
 & 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' -X utf8 (Join-Path $PSScriptRoot 'audit-provider.py')
 if($LASTEXITCODE -ne 0){throw 'Independent provider outcome audit failed'}
 $taskStages+=@{stage='provider';runtimeAndAuditCompleted=$true;outcomes='SEE_PROVIDER_VERIFICATION_NOT_INFERRED_PASS'}
 $taskEntry.state='FOLLOWUPS_COMPLETED_IMAGE_REVIEW_AND_DESKTOP_PERFORMANCE_REMAIN'
} catch {
 $taskEntry.state='STOPPED_AT_FAILED_GATE_OR_RUNTIME'
 $taskEntry.error=$_.Exception.Message
 throw
} finally {
 $taskEntry.completedUtc=[DateTimeOffset]::UtcNow.ToString('o')
 $taskEntry.stages=$taskStages
 $taskEntry|ConvertTo-Json -Depth 8|Set-Content $taskTerminal
}
