$ErrorActionPreference='Stop'
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx'
$taskDraft=Join-Path $taskRoot 'artifacts/af1082-f5-d7-local87cu1-1'
. (Join-Path $PSScriptRoot 'require-current-return.ps1')
Assert-CurrentSdkNativeReturn -TaskRoot $taskRoot
$taskProof=Get-Content (Join-Path $taskDraft 'freeze.json') -Raw|ConvertFrom-Json -DateKind String
$taskOriginalSources=(Get-Content (Join-Path $taskDraft 'followup-preparation.json') -Raw|ConvertFrom-Json).originalModelPromptAndAssertionSources
$taskProvider=$taskProof.provider
$taskTerminal=Join-Path $taskDraft 'provider-both-exit.json'
$taskFreezePath=Join-Path $taskDraft 'provider-runtime-freeze.json'
if((Test-Path $taskTerminal)-or(Test-Path $taskFreezePath)){throw 'Immutable provider runtime exists; inspect its handle'}
if(!(Test-Path (Join-Path $taskDraft 'sdk-native-return.json'))){throw 'SDK native lane not handed back'}
$taskAudit=Get-Content (Join-Path $taskRoot 'capability-evidence/diagnostics/af1082-f5-d7-local87cu1/full-verification.json') -Raw|ConvertFrom-Json -DateKind String
if($taskAudit.configurations.Count -ne 2 -or @($taskAudit.configurations|Where-Object {$_.total -ne 7117 -or $_.failed -ne 24 -or $_.skipped -ne 0 -or $_.newFailureNames.Count -ne 0 -or !$_.actualNative62Matched}).Count -ne 0){throw 'Final full7117 scope or failures changed'}
foreach($taskRow in $taskProof.sourcePayload){if((Get-FileHash -LiteralPath (Join-Path $taskProof.workspace $taskRow.path)).Hash -cne $taskRow.sha256){throw 'CurrentF5D7 source drift'}}
foreach($taskRow in $taskProof.providerPayload){if((Get-FileHash -LiteralPath (Join-Path $taskProvider $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Final provider payload drift'}}
foreach($taskRow in $taskOriginalSources){if((Get-FileHash -LiteralPath (Join-Path $taskRoot $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Original prompts/assertions differ'}}
. (Join-Path $taskRoot 'artifacts/assert-cad-native-consumers-idle-2.ps1')
. (Join-Path $taskRoot 'artifacts/wait-af-native-consumers-idle-1.ps1')
$null=Wait-AFNativeConsumersIdle -EvidenceDirectory (Join-Path $taskDraft 'provider-native-idle-observations-3')
$taskModels=@(((& 'C:/Users/yoiri/.lmstudio/bin/lms.exe' ps --json)|ConvertFrom-Json))
$taskSelected=@($taskModels|Where-Object identifier -CEQ 'prism-ml/bonsai-27b')
if($taskSelected.Count -ne 1 -or $taskSelected[0].contextLength -ne 8192 -or $taskSelected[0].selectedVariant -cne 'prism-ml/bonsai-27b@q1_0'){throw 'Original model and context are not actually loaded'}
$taskStatus=(& 'C:/Users/yoiri/.lmstudio/bin/lms.exe' server status --json)|ConvertFrom-Json -DateKind String
if(!$taskStatus.running -or $taskStatus.port -ne 15630){throw 'Observed local endpoint changed'}
$taskPayload=@(Get-ChildItem -LiteralPath $taskProvider -Recurse -File|ForEach-Object {@{path=[IO.Path]::GetRelativePath($taskProvider,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
@{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');candidate=$taskProvider;payload=$taskPayload;native62=$taskProof.native62;model=$taskSelected[0];endpoint='http://127.0.0.1:15630/v1';scenarioOrder=@('drawing','workspace');originalPromptAndAssertionSources=$taskOriginalSources;originalContext=8192;originalToolRounds=16;originalPerTurnMinutes=4;parentFullBothAudit='capability-evidence/diagnostics/af1082-f5-d7-local87cu1/full-verification.json';parentKnownSTEP24FailuresPreserved=$true}|ConvertTo-Json -Depth 7|Set-Content -LiteralPath $taskFreezePath -Encoding utf8
. (Join-Path $taskRoot 'artifacts/assert-cad-native-consumers-idle-2.ps1')
. (Join-Path $taskRoot 'artifacts/a5-native-binding-helper-1.ps1')
$taskStarted=[DateTimeOffset]::UtcNow;$taskRows=@()
foreach($taskScenario in @('drawing','workspace')){
 $null=Wait-AFNativeConsumersIdle -EvidenceDirectory (Join-Path $taskDraft 'provider-native-idle-observations-3')
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
   if(!(Test-Path -LiteralPath $taskBinding)){$null=Save-A5NativeBinding -ProcessId $taskProcess.Id -NativeDirectory (Join-Path $taskProvider 'occt') -Expected $taskProof.native62 -OutputPath $taskBinding}
  }
  $taskCode=$taskProcess.ExitCode
 }finally{$null=$taskCopy.GetAwaiter().GetResult();$null=$taskErrorCopy.GetAwaiter().GetResult();$taskOut.Dispose();$taskErr.Dispose();$taskProcess.Dispose()}
 @{started=$taskScenarioStart.ToString('o');completed=[DateTimeOffset]::UtcNow.ToString('o');exitCode=$taskCode;scenario=$taskScenario;binding=$taskBinding;candidate=$taskProvider;provider='lmstudio';model='prism-ml/bonsai-27b';endpoint='http://127.0.0.1:15630/v1';contextLength=8192;maxToolRounds=16;originalPerTurnMinutes=4;desktopInputStarted=$false;productionApplied=$false;publiclyPublished=$false;cleanup=$false}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $taskExit -Encoding utf8
 Get-Content -LiteralPath ($taskOutput+'.log') -Tail 3
 if($taskCode -eq 0 -and !(Test-Path -LiteralPath $taskBinding)){throw 'Provider pass lacks actual native62 evidence'}
 $taskRows+=@{scenario=$taskScenario;exitCode=$taskCode;terminal=$taskExit;sha256=(Get-FileHash -LiteralPath $taskExit).Hash}
}
@{started=$taskStarted.ToString('o');completed=[DateTimeOffset]::UtcNow.ToString('o');scenarios=$taskRows;freeze=$taskFreezePath;desktopInputStarted=$false;productionApplied=$false;publiclyPublished=$false;cleanup=$false;AF='ACTIVE';G='USER_DEFERRED'}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $taskTerminal -Encoding utf8
