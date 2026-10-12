$ErrorActionPreference='Stop'
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx';$taskDraft=Join-Path $taskRoot 'artifacts/d6-finite-newbody-draft-4'
$taskDependency=Join-Path $taskRoot 'artifacts/b18-prescribed-complete-authors-draft-2/full-both-1-exit.json'
$taskExit=Join-Path $taskDraft 'full-both-1-exit.json'
if(Test-Path -LiteralPath $taskExit){throw 'Immutable D6 full suite exists'}
$taskStarted=[DateTimeOffset]::UtcNow;$taskResults=@()
try {
 if(!(Test-Path -LiteralPath $taskDependency)) {
  $taskMatches=@(Get-CimInstance Win32_Process -Filter "Name='pwsh.exe'"|Where-Object {$_.CommandLine -match ' -File artifacts/run-b18-solver21-full-after-provider-1\.ps1$'})
  if($taskMatches.Count -ne 1){throw 'Inspect the original solver21 coordinator before proceeding'}
  $taskDependencyProcess=Get-Process -Id $taskMatches[0].ProcessId -ErrorAction Stop
  try{while(!(Test-Path -LiteralPath $taskDependency)){if($taskDependencyProcess.HasExited){throw 'Solver21 coordinator exited without terminal; preserve all evidence and inspect'};$null=$taskDependencyProcess.WaitForExit(5000)}}finally{$taskDependencyProcess.Dispose()}
 }
 $taskOld=Get-Content -LiteralPath $taskDependency -Raw|ConvertFrom-Json
 if(@($taskOld.gates).Count -ne 2){throw 'Original solver21 two-configuration scope incomplete'}
 foreach($taskOldGate in $taskOld.gates){if((Get-FileHash -LiteralPath $taskOldGate.terminal).Hash -cne $taskOldGate.sha256){throw 'Dependency terminal drift'}}
 $taskPwsh='C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/native/powershell/pwsh.exe'
 foreach($taskConfiguration in @('Release','Debug')) {
  & $taskPwsh -NoProfile -File (Join-Path $taskRoot 'artifacts/run-d6-full6619-gate-1.ps1') -Configuration $taskConfiguration -Group full6619
  $taskCode=$LASTEXITCODE;$taskTerminal=Join-Path $taskDraft ('full6619-'+$taskConfiguration.ToLower()+'-1/exit.json')
  if(!(Test-Path -LiteralPath $taskTerminal)){throw 'No actual D6 full terminal; inspect before continuing'}
  $taskActual=Get-Content -LiteralPath $taskTerminal -Raw|ConvertFrom-Json
  if($taskActual.exitCode -ne $taskCode){throw 'Actual full terminal and process disagree'}
  $taskResults+=@{configuration=$taskConfiguration;exitCode=$taskCode;terminal=$taskTerminal;sha256=(Get-FileHash -LiteralPath $taskTerminal).Hash}
 }
 @{started=$taskStarted.ToString('o');completed=[DateTimeOffset]::UtcNow.ToString('o');dependency=$taskDependency;dependencySha256=(Get-FileHash -LiteralPath $taskDependency).Hash;gates=$taskResults;productionApplied=$false;cleanup=$false;desktopInputStarted=$false;AF='ACTIVE';G='USER_DEFERRED'}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $taskExit -Encoding utf8
} catch {
 $taskFailure=Join-Path $taskDraft 'full-queue-failure-1.json'
 if(!(Test-Path -LiteralPath $taskFailure)){@{started=$taskStarted.ToString('o');recordedAt=[DateTimeOffset]::UtcNow.ToString('o');error=$_.ToString();completedGates=$taskResults;productionApplied=$false;cleanup=$false;AF='ACTIVE'}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $taskFailure -Encoding utf8}
 throw
}
