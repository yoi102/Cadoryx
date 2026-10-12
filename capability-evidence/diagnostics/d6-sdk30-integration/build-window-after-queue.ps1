$ErrorActionPreference='Stop'
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx'
$taskBuild=Join-Path $taskRoot 'artifacts/d6-sdk30-window-host-1'
$taskPrep=Get-Content -LiteralPath (Join-Path $taskBuild 'preparation-1.json') -Raw|ConvertFrom-Json
$taskStarted=[DateTimeOffset]::UtcNow
try{
 foreach($taskRow in $taskPrep.source){if((Get-FileHash -LiteralPath $taskRow.path).Hash -cne $taskRow.sha256){throw 'Frozen SDK30 observer source drift'}}
 $taskQueue=Join-Path $taskRoot 'artifacts/af-status225-runtime-queue-1'
 $taskQueueTerminal=Join-Path $taskQueue 'exit.json'
 if(!(Test-Path -LiteralPath $taskQueueTerminal)){
  $taskStart=Get-Content -LiteralPath (Join-Path $taskQueue 'start.json') -Raw|ConvertFrom-Json
  $taskMetadata=Get-CimInstance Win32_Process -Filter ('ProcessId='+$taskStart.pid)
  if(!$taskMetadata -or $taskMetadata.CommandLine -notlike '*capability-evidence/diagnostics/d6-sdk30-integration/run-queue.ps1*'){throw 'Original runtime queue not live; inspect without restarting'}
  $taskProcess=Get-Process -Id $taskStart.pid -ErrorAction Stop
  try{
   while(!(Test-Path -LiteralPath $taskQueueTerminal)){
    if($taskProcess.HasExited){throw 'Original runtime queue exited without terminal; preserve outputs'}
    if(Test-Path -LiteralPath (Join-Path $taskQueue 'failure.json')){throw 'Runtime queue failure requires inspection before observer build'}
    $null=$taskProcess.WaitForExit(5000)
   }
  }finally{$taskProcess.Dispose()}
 }
 $taskComplete=Get-Content -LiteralPath $taskQueueTerminal -Raw|ConvertFrom-Json
 if(@($taskComplete.gates).Count -ne 10){throw 'Original runtime scopes not complete'}
 foreach($taskGate in $taskComplete.gates){
  if((Get-FileHash -LiteralPath $taskGate.audit).Hash -cne $taskGate.auditSha256 -or (Get-FileHash -LiteralPath $taskGate.terminal).Hash -cne $taskGate.terminalSha256){throw 'Runtime terminal/audit drift'}
 }
 foreach($taskConfiguration in @('Release','Debug')){
  $taskFull=Get-Content -LiteralPath (Join-Path $taskRoot ('artifacts/d6-sdk30-production-integration-1/full6530-'+$taskConfiguration.ToLower()+'-1-verification.json')) -Raw|ConvertFrom-Json
  if($taskFull.passed -ne 6530 -or $taskFull.failed -ne 0 -or $taskFull.skipped -ne 0){throw 'SDK30 production combination full gate failed'}
  if((Get-FileHash -LiteralPath $taskPrep.candidateBuild.path).Hash -cne $taskPrep.candidateBuild.sha256 -or $taskFull.build.sha256 -cne $taskPrep.candidateBuild.sha256){throw 'SDK30 combination build differs'}
 }
 . (Join-Path $taskRoot 'artifacts/wait-cad-native-consumers-idle-1.ps1')
 $null=Wait-CadNativeConsumersIdle
 $taskResults=@()
 foreach($taskConfiguration in @('Release','Debug')){
  $taskLabel=$taskConfiguration.ToLower()
  $taskTerminal=Join-Path $taskBuild ('build-'+$taskLabel+'-1-exit.json')
  if(Test-Path -LiteralPath $taskTerminal){throw 'Immutable SDK30 observer build exists'}
  dotnet build (Join-Path $taskBuild 'WindowHost.csproj') -c $taskConfiguration ('-p:CandidatePayload='+$taskRoot+'/artifacts/d6-sdk30-production-integration-1/workspace-1/Cadoryx.wpf/bin/'+$taskConfiguration+'/net10.0-windows/win-x64') --nologo -v minimal > (Join-Path $taskBuild ('build-'+$taskLabel+'-1.log')) 2> (Join-Path $taskBuild ('build-'+$taskLabel+'-1-stderr.log'))
  $taskCode=$LASTEXITCODE
  @{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');exitCode=$taskCode;actualWindowsStarted=$false;cleanup=$false}|ConvertTo-Json|Set-Content -LiteralPath $taskTerminal -Encoding utf8
  $taskResults+=@{configuration=$taskConfiguration;exitCode=$taskCode;terminal=$taskTerminal;sha256=(Get-FileHash -LiteralPath $taskTerminal).Hash}
  if($taskCode -ne 0){throw 'SDK30 observer compile failed; original log preserved'}
 }
 @{started=$taskStarted.ToString('o');completed=[DateTimeOffset]::UtcNow.ToString('o');gates=$taskResults;runtimeQueueTerminal=$taskQueueTerminal;runtimeQueueSha256=(Get-FileHash -LiteralPath $taskQueueTerminal).Hash;actualWindowsStarted=$false;cleanup=$false;AF='ACTIVE';G='USER_DEFERRED'}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $taskBuild 'build-both-1-exit.json') -Encoding utf8
 & 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' -X utf8 (Join-Path $taskBuild 'build-audit.py')
 if($LASTEXITCODE -ne 0){throw 'SDK30 observer build audit failed'}
}catch{
 $taskFailure=Join-Path $taskBuild 'build-queue-failure-1.json'
 if(!(Test-Path -LiteralPath $taskFailure)){@{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');error=$_.ToString();actualWindowsStarted=$false;cleanup=$false;AF='ACTIVE'}|ConvertTo-Json|Set-Content -LiteralPath $taskFailure -Encoding utf8}
 throw
}
