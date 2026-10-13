$ErrorActionPreference='Stop'
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx'
$taskDraft=Join-Path $taskRoot 'artifacts/af1081-local86-1'
$taskEvidence=Join-Path $taskRoot 'capability-evidence/diagnostics/af1081-local86'
$taskProof=Get-Content (Join-Path $taskEvidence 'far90-build-verification.json') -Raw|ConvertFrom-Json -DateKind String
$taskDebug=Get-Content (Join-Path $taskDraft 'debug-resume-coordinator-1-start.json') -Raw|ConvertFrom-Json -DateKind String
$taskAuditPath=Join-Path $taskEvidence 'full-verification.json'
$taskState=Join-Path $taskDraft 'far90-followup-state.json'
if(Test-Path $taskState){throw 'Follow-up exists; inspect its actual handle'}
function Save-FollowupState([string]$state){
 [ordered]@{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');coordinatorPid=$PID;state=$state;AFComplete=$false;desktopInputStarted=$false}|ConvertTo-Json|Set-Content $taskState
}
try {
Save-FollowupState 'WAITING_FOR_OBSERVED_DEBUG6830_TERMINAL_AUDIT'
while(!(Test-Path $taskAuditPath)){
 $taskObserved=Get-Process -Id $taskDebug.pid -ErrorAction SilentlyContinue
 if(!$taskObserved){
  # Recheck the audit after observing the coordinator's actual disappearance.
  if(Test-Path $taskAuditPath){break}
  throw 'Observed Debug coordinator disappeared without full audit; do not start another native consumer'
 }
 if($taskObserved.StartTime.ToUniversalTime().ToString('o') -cne ([DateTimeOffset]::Parse($taskDebug.processStarted).UtcDateTime.ToString('o'))){throw 'Coordinator PID was reused'}
 Start-Sleep -Seconds 5
}
$taskFull=Get-Content $taskAuditPath -Raw|ConvertFrom-Json -DateKind String
$taskDeferred=Get-Content (Join-Path $taskEvidence 'step24-user-deferral.json') -Raw|ConvertFrom-Json -DateKind String
if($taskFull.configurations.Count -ne 2){throw 'Missing full two-configuration scope'}
foreach($taskConfig in $taskFull.configurations){
 if($taskConfig.total -ne 6830 -or $taskConfig.failed -ne 24 -or $taskConfig.skipped -ne 0 -or $taskConfig.newFailureNames.Count -ne 0 -or !$taskConfig.actualNative62Matched){throw 'Full scope or failure set changed; inspect it'}
 if(@(Compare-Object @($taskDeferred.exactTestNames) @($taskConfig.failures|ForEach-Object name)).Count -ne 0){throw 'Do not defer unapproved failures'}
}
. (Join-Path $taskRoot 'artifacts/assert-cad-native-consumers-idle-2.ps1')
Save-FollowupState 'RUNNING_FAR90_PREREQUISITE_SERIAL_AFTER_FULL_DEBUG'
foreach($taskBuild in $taskProof.builds){
 $taskIdle=@(Assert-CadNativeConsumersIdle)
 foreach($taskInput in $taskProof.sourceInputs){if((Get-FileHash (Join-Path $taskRoot $taskInput.path)).Hash -cne $taskInput.sha256){throw 'Far90 source changed after build'}}
 foreach($taskRow in $taskBuild.payload){if((Get-FileHash (Join-Path $taskBuild.folder $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Far90 built payload changed'}}
 $taskOutput=Join-Path $taskDraft ('far-prerequisite/runtime-'+$taskBuild.configuration.ToLower()+'-5')
 if(Test-Path $taskOutput){throw 'Preserve an existing run'}
 $taskInfo=[Diagnostics.ProcessStartInfo]::new((Get-Command dotnet).Source)
 $taskInfo.WorkingDirectory=$taskRoot;$taskInfo.UseShellExecute=$false;$taskInfo.CreateNoWindow=$true;$taskInfo.WindowStyle='Hidden'
 $taskInfo.RedirectStandardOutput=$true;$taskInfo.RedirectStandardError=$true
 foreach($taskArg in @((Join-Path $taskBuild.folder 'FiniteSurfaceFarProbe.dll'),$taskBuild.candidate,$taskOutput)){$taskInfo.ArgumentList.Add($taskArg)}
 $taskStarted=[DateTimeOffset]::UtcNow;$taskProcess=[Diagnostics.Process]::Start($taskInfo)
 $taskProcess.Refresh()
 @{recordedAt=$taskStarted.ToString('o');pid=$taskProcess.Id;processStarted=$taskProcess.StartTime.ToUniversalTime().ToString('o');coordinatorPid=$PID;configuration=$taskBuild.configuration;output=$taskOutput;unrelatedProcessesPreserved=$taskIdle}|ConvertTo-Json -Depth 5|Set-Content ($taskOutput+'-start.json')
 Write-Output ('Far90 '+$taskBuild.configuration+' started PID='+$taskProcess.Id)
 $taskOut=[IO.File]::Open($taskOutput+'.log',[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
 $taskErr=[IO.File]::Open($taskOutput+'-stderr.log',[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
 $taskCopy=$taskProcess.StandardOutput.BaseStream.CopyToAsync($taskOut);$taskErrorCopy=$taskProcess.StandardError.BaseStream.CopyToAsync($taskErr)
 try{$taskProcess.WaitForExit();$taskCode=$taskProcess.ExitCode}
 finally{$null=$taskCopy.GetAwaiter().GetResult();$null=$taskErrorCopy.GetAwaiter().GetResult();$taskOut.Dispose();$taskErr.Dispose();$taskProcess.Dispose()}
 if(!(Test-Path $taskOutput)){throw 'Far90 failed before creating its output; preserve logs'}
 @{started=$taskStarted.ToString('o');completed=[DateTimeOffset]::UtcNow.ToString('o');exitCode=$taskCode;configuration=$taskBuild.configuration;output=$taskOutput;AFComplete=$false}|ConvertTo-Json|Set-Content (Join-Path $taskOutput 'exit.json')
 if(!(Test-Path (Join-Path $taskOutput 'result.json'))){throw 'Far90 has no complete result; preserve partial evidence'}
 Get-Content ($taskOutput+'.log') -Tail 2
}
& 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' -X utf8 (Join-Path $taskRoot 'tools/FiniteSurfaceFarProbe/audit-runtime.py')
if($LASTEXITCODE -ne 0){throw 'Far90 runtime audit failed; preserve all output'}
Save-FollowupState 'FAR90_RUNTIME_AUDITED_STARTING_ORIGINAL_MODEL_SCENARIOS'
$taskIdle=@(Assert-CadNativeConsumersIdle)
$taskResumePath=Join-Path $taskDraft 'provider-native-resume.json'
if(Test-Path $taskResumePath){throw 'Preserve previous provider resume evidence'}
$taskHandoffPath=Join-Path $taskDraft 'debug-native-resume.json'
$taskHandoff=Get-Content $taskHandoffPath -Raw|ConvertFrom-Json -DateKind String
if(!$taskHandoff.sdkExplicitHandoffReceived -or $taskHandoff.candidatePackage -cne '8.0.1-preview.86'){throw 'SDK hand-back evidence missing'}
@{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');sdkExplicitHandoffReceived=$true;actualCadConsumersIdle=$true;candidatePackage='8.0.1-preview.86';handoff=$taskHandoffPath;handoffSha256=(Get-FileHash $taskHandoffPath).Hash;unrelatedProcessesPreserved=$taskIdle;priorFullAudit=$taskAuditPath;known24Deferred=$true}|ConvertTo-Json -Depth 5|Set-Content $taskResumePath
& (Join-Path $taskEvidence 'run-provider.ps1')
Save-FollowupState 'FAR90_AND_ORIGINAL_MODEL_SCENARIOS_TERMINAL_NOT_PRODUCT_ACCEPTANCE'
} catch {
 Save-FollowupState ('STOPPED_WITH_EVIDENCE_PRESERVED: '+$_.Exception.Message)
 throw
}
