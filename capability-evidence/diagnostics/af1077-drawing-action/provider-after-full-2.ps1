$ErrorActionPreference='Stop'
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx'
$taskDraft=Join-Path $taskRoot 'artifacts/af1077-drawing-action-1'
$taskProvider=Join-Path $taskDraft 'provider'
$taskTerminal=Join-Path $taskDraft 'provider-both-exit.json'
$taskFreezePath=Join-Path $taskDraft 'provider-runtime-freeze.json'
if((Test-Path $taskTerminal)-or(Test-Path $taskFreezePath)){throw 'Immutable provider runtime exists; inspect its handle'}
$taskProof=Get-Content (Join-Path $taskRoot 'capability-evidence/diagnostics/af1077-drawing-action/verification.json') -Raw|ConvertFrom-Json
$taskPreparation=Get-Content (Join-Path $taskDraft 'preparation.json') -Raw|ConvertFrom-Json
$taskParent=Get-Content (Join-Path $taskRoot 'artifacts/af1076-root-full-1/freeze.json') -Raw|ConvertFrom-Json
$taskQueueStart=Join-Path $taskDraft 'provider-queue-2-start.json'
if(Test-Path $taskQueueStart){throw 'Immutable queue exists; observe its actual process instead'}
$taskCoordinator=Get-CimInstance Win32_Process -Filter 'ProcessId=3096'
if(!$taskCoordinator -or $taskCoordinator.Name -cne 'pwsh.exe' -or $taskCoordinator.CommandLine -notmatch 'run-af1076-root-full-both-1.ps1'){throw 'Original full-run coordinator no longer matches'}
$taskCoordinatorStarted=$taskCoordinator.CreationDate
@{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');waitingForPid=3096;creationDate=$taskCoordinatorStarted;queuePid=$PID;fullRunMustFinish=$true;nativeStarted=$false}|ConvertTo-Json|Set-Content $taskQueueStart
Write-Output 'Queued original1077 provider scenarios behind actual full6652 coordinator PID3096.'
$taskFullAudit=Join-Path $taskRoot 'capability-evidence/diagnostics/af1076-root-full/verification.json'
while(!(Test-Path $taskFullAudit)){
 $taskLive=Get-CimInstance Win32_Process -Filter 'ProcessId=3096'
 if(!$taskLive -or $taskLive.CreationDate -ne $taskCoordinatorStarted){throw 'Full coordinator ended without completed independent audit; preserve outputs, do not restart'}
 Start-Sleep -Seconds 2
}
$taskAudit=Get-Content $taskFullAudit -Raw|ConvertFrom-Json
if($taskAudit.configurations.Count -ne 2 -or @($taskAudit.configurations|Where-Object {$_.total -ne 6652 -or $_.failed -ne 24 -or $_.skipped -ne 0 -or $_.newFailureNames.Count -ne 0 -or !$_.actualNative62Matched}).Count -ne 0){throw 'Completed6652 changed failures or scope; inspect before provider native use'}
foreach($taskRow in $taskPreparation.payload){if((Get-FileHash (Join-Path $taskPreparation.workspace $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Candidate source drift'}}
foreach($taskRow in $taskProof.providerPayload){if((Get-FileHash (Join-Path $taskProvider $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Audited provider payload drift'}}
$taskSources=@()
foreach($taskRow in $taskProof.originalProviderSource3){
 $taskSource=Join-Path $taskPreparation.workspace ('tools/CadAgentWorkflowProbe/'+$taskRow.name)
 if((Get-FileHash $taskSource).Hash -cne $taskRow.sha256){throw 'Original provider prompts/assertions drift'}
 $taskSources+=@{path=[IO.Path]::GetRelativePath($taskRoot,$taskSource);sha256=$taskRow.sha256}
}
$taskProof|Add-Member -NotePropertyName sources -NotePropertyValue $taskSources
. (Join-Path $taskRoot 'artifacts/assert-cad-native-consumers-idle-2.ps1')
$null=Assert-CadNativeConsumersIdle
$taskModels=@(((& 'C:/Users/yoiri/.lmstudio/bin/lms.exe' ps --json)|ConvertFrom-Json))
$taskSelected=@($taskModels|Where-Object identifier -CEQ 'prism-ml/bonsai-27b')
if($taskSelected.Count -ne 1 -or $taskSelected[0].contextLength -ne 8192 -or $taskSelected[0].selectedVariant -cne 'prism-ml/bonsai-27b@q1_0'){throw 'Original model and context are not actually loaded'}
$taskStatus=(& 'C:/Users/yoiri/.lmstudio/bin/lms.exe' server status --json)|ConvertFrom-Json
if(!$taskStatus.running -or $taskStatus.port -ne 15630){throw 'Observed local endpoint changed'}
$taskPayload=@(Get-ChildItem -LiteralPath $taskProvider -Recurse -File|ForEach-Object {@{path=[IO.Path]::GetRelativePath($taskProvider,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
@{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');candidate=$taskProvider;payload=$taskPayload;native62=$taskParent.native62;model=$taskSelected[0];endpoint='http://127.0.0.1:15630/v1';scenarioOrder=@('drawing','workspace');originalPromptAndAssertionSources=$taskProof.sources;originalContext=8192;originalToolRounds=16;originalPerTurnMinutes=4;parentFullBothAudit='capability-evidence/diagnostics/af1076-root-full/verification.json';parentKnownSTEP24FailuresPreserved=$true}|ConvertTo-Json -Depth 7|Set-Content -LiteralPath $taskFreezePath -Encoding utf8
. (Join-Path $taskRoot 'artifacts/assert-cad-native-consumers-idle-2.ps1')
. (Join-Path $taskRoot 'artifacts/a5-native-binding-helper-1.ps1')
$taskStarted=[DateTimeOffset]::UtcNow;$taskRows=@()
foreach($taskScenario in @('drawing','workspace')){
 $null=Assert-CadNativeConsumersIdle
 $taskOutput=Join-Path $taskDraft ('lmstudio-'+$taskScenario+'-1')
 $taskExit=$taskOutput+'-exit.json';$taskBinding=$taskOutput+'-loaded-native.json'
 if((Test-Path -LiteralPath $taskOutput)-or(Test-Path -LiteralPath $taskExit)){throw 'Immutable scenario exists'}
 foreach($taskRow in $taskPayload){if((Get-FileHash -LiteralPath (Join-Path $taskProvider $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Actual provider payload drift'}}
 $taskStart=[Diagnostics.ProcessStartInfo]::new((Get-Command dotnet).Source)
 $taskStart.WorkingDirectory=$taskProvider;$taskStart.UseShellExecute=$false;$taskStart.CreateNoWindow=$true;$taskStart.WindowStyle='Hidden'
 $taskStart.RedirectStandardOutput=$true;$taskStart.RedirectStandardError=$true
 foreach($taskArg in @((Join-Path $taskProvider 'CadAgentWorkflowProbe.dll'),'--provider','lmstudio','--endpoint','http://127.0.0.1:15630/v1','--model','prism-ml/bonsai-27b','--scenario',$taskScenario,'--output',$taskOutput)){$taskStart.ArgumentList.Add($taskArg)}
 $taskScenarioStart=[DateTimeOffset]::UtcNow;$taskProcess=[Diagnostics.Process]::Start($taskStart)
 $taskOut=[IO.File]::Open($taskOutput+'.log',[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
 $taskErr=[IO.File]::Open($taskOutput+'-stderr.log',[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
 $taskCopy=$taskProcess.StandardOutput.BaseStream.CopyToAsync($taskOut);$taskErrorCopy=$taskProcess.StandardError.BaseStream.CopyToAsync($taskErr)
 Write-Output ('Original Bonsai '+$taskScenario+' started; PID='+$taskProcess.Id)
 @{started=$taskScenarioStart.ToString('o');pid=$taskProcess.Id;scenario=$taskScenario;output=$taskOutput;candidate=$taskProvider}|ConvertTo-Json|Set-Content -LiteralPath ($taskOutput+'-start.json') -Encoding utf8
 try{
  while(!$taskProcess.WaitForExit(100)){
   if(!(Test-Path -LiteralPath $taskBinding)){$null=Save-A5NativeBinding -ProcessId $taskProcess.Id -NativeDirectory (Join-Path $taskProvider 'occt') -Expected $taskParent.native62 -OutputPath $taskBinding}
  }
  $taskCode=$taskProcess.ExitCode
 }finally{$null=$taskCopy.GetAwaiter().GetResult();$null=$taskErrorCopy.GetAwaiter().GetResult();$taskOut.Dispose();$taskErr.Dispose();$taskProcess.Dispose()}
 @{started=$taskScenarioStart.ToString('o');completed=[DateTimeOffset]::UtcNow.ToString('o');exitCode=$taskCode;scenario=$taskScenario;binding=$taskBinding;candidate=$taskProvider;provider='lmstudio';model='prism-ml/bonsai-27b';endpoint='http://127.0.0.1:15630/v1';contextLength=8192;maxToolRounds=16;originalPerTurnMinutes=4;desktopInputStarted=$false;productionApplied=$false;cleanup=$false}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $taskExit -Encoding utf8
 Get-Content -LiteralPath ($taskOutput+'.log') -Tail 3
 if($taskCode -eq 0 -and !(Test-Path -LiteralPath $taskBinding)){throw 'Provider pass lacks actual native62 evidence'}
 $taskRows+=@{scenario=$taskScenario;exitCode=$taskCode;terminal=$taskExit;sha256=(Get-FileHash -LiteralPath $taskExit).Hash}
}
@{started=$taskStarted.ToString('o');completed=[DateTimeOffset]::UtcNow.ToString('o');scenarios=$taskRows;freeze=$taskFreezePath;desktopInputStarted=$false;productionApplied=$false;cleanup=$false;AF='ACTIVE';G='USER_DEFERRED'}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $taskTerminal -Encoding utf8


