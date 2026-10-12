$ErrorActionPreference='Stop'
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx'
$taskQueue=Join-Path $taskRoot 'artifacts/af-status225-runtime-queue-1'
$taskPwsh='C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/native/powershell/pwsh.exe'
$taskStarted=[DateTimeOffset]::UtcNow
$taskGates=@()
try{
 $taskSchedule=@(
  @{draft='d6-sdk30-production-integration-1';groups=@('finite99','agent165','full6530');attempt=1},
  @{draft='b18-prescribed-complete-authors-draft-2';groups=@('full6556');attempt=2},
  @{draft='d6-finite-newbody-draft-4';groups=@('full6619');attempt=1}
 )
 foreach($taskStage in $taskSchedule){
  foreach($taskGroup in $taskStage.groups){
   foreach($taskConfig in @('Release','Debug')){
    & $taskPwsh -NoProfile -File (Join-Path $PSScriptRoot 'run-gate.ps1') -Draft $taskStage.draft -Configuration $taskConfig -Group $taskGroup -Attempt $taskStage.attempt
    $taskCode=$LASTEXITCODE
    $taskName=$taskGroup+'-'+$taskConfig.ToLower()+'-'+$taskStage.attempt
    $taskDraft=Join-Path $taskRoot ('artifacts/'+$taskStage.draft)
    $taskAudit=Join-Path $taskDraft ($taskName+'-verification.json')
    $taskTerminal=Join-Path $taskDraft ($taskName+'/exit.json')
    if(!(Test-Path -LiteralPath $taskAudit) -or !(Test-Path -LiteralPath $taskTerminal)){throw 'Missing actual runtime/audit terminal'}
    $taskActual=Get-Content -LiteralPath $taskTerminal -Raw|ConvertFrom-Json
    if($taskActual.exitCode -ne $taskCode){throw 'Process and terminal disagree'}
    $taskGates+=@{draft=$taskStage.draft;group=$taskGroup;configuration=$taskConfig;attempt=$taskStage.attempt;exitCode=$taskCode;audit=$taskAudit;auditSha256=(Get-FileHash -LiteralPath $taskAudit).Hash;terminal=$taskTerminal;terminalSha256=(Get-FileHash -LiteralPath $taskTerminal).Hash}
    @{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');gates=$taskGates;cleanup=$false;AF='ACTIVE'}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $taskQueue 'progress.json') -Encoding utf8
    if($taskGroup -in @('finite99','agent165') -and $taskCode -ne 0){throw 'Production combination focused failure; diagnose before full suite'}
   }
  }
 }
 @{started=$taskStarted.ToString('o');completed=[DateTimeOffset]::UtcNow.ToString('o');gates=$taskGates;productionApplied=$false;cleanup=$false;desktopInputStarted=$false;AF='ACTIVE';G='USER_DEFERRED'}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $taskQueue 'exit.json') -Encoding utf8
}catch{
 @{started=$taskStarted.ToString('o');recordedAt=[DateTimeOffset]::UtcNow.ToString('o');error=$_.ToString();completedGates=$taskGates;cleanup=$false;AF='ACTIVE'}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $taskQueue 'failure.json') -Encoding utf8
 throw
}
