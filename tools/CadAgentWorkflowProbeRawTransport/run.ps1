[CmdletBinding()]
param([ValidateSet('drawing','workspace')][string]$Scenario='drawing',[ValidateRange(1,100)][int]$Iteration=1,[ValidateRange(2,100)][int]$Qualification=4)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskStage=Join-Path $taskRoot 'artifacts/f5-lmstudio-completion-integrity-1'
. (Join-Path $taskRoot 'tools/CurrentCadCandidate/require-current-return.ps1')
Assert-CurrentSdkNativeReturn -TaskRoot $taskRoot
$taskQualificationPath=Join-Path $taskStage ('qualification'+$Qualification+'.json')
$taskProof=Get-Content $taskQualificationPath -Raw|ConvertFrom-Json -DateKind String
if($taskProof.state -cnotin @('F5_LMSTUDIO_PARSER_ONLY_DELTA_32_BOTH_86_RELATED_BOTH_RAW_PROVIDER_BUILD0_NOT_MODEL_RUNTIME','F5_BOUNDED_TRUNCATION_RECOVERY_39_BOTH_96_RELATED_BOTH_RAW_PROVIDER_BUILD0_NOT_MODEL_RUNTIME','F5_BOUNDED_TRUNCATION_RECOVERY_40_BOTH_97_RELATED_BOTH_RAW_PROVIDER_BUILD0_NOT_MODEL_RUNTIME') -or !$taskProof.originalPromptsAssertions8192_16_4MinutesRetained){throw 'Qualified raw provider required'}
$taskSourceRefs=if($taskQualification -eq 2){@($taskProof.clientSource)}else{@($taskProof.qualifiedSources)}
foreach($taskReference in $taskSourceRefs+@($taskProof.projection)+@($taskProof.builds)){
 if((Get-FileHash (Join-Path $taskRoot $taskReference.path)).Hash -cne $taskReference.sha256){throw 'Parser or fixture/build source drift'}
}
$taskProjection=Get-Content (Join-Path $taskRoot $taskProof.projection.path) -Raw|ConvertFrom-Json -DateKind String
foreach($taskReference in @($taskProjection.originals)+@($taskProjection.projected)){
 if((Get-FileHash (Join-Path $taskRoot $taskReference.path)).Hash -cne $taskReference.sha256){throw 'Original workflows or recording-only projection drift'}
}
foreach($taskRow in $taskProof.providerPayload){if((Get-FileHash (Join-Path $taskProof.provider $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Qualified raw provider payload changed'}}
$taskModels=@(((& 'C:/Users/yoiri/.lmstudio/bin/lms.exe' ps --json)|ConvertFrom-Json))
$taskModel=@($taskModels|Where-Object identifier -CEQ 'prism-ml/bonsai-27b')
if($taskModel.Count -ne 1 -or $taskModel[0].contextLength -ne 8192 -or $taskModel[0].selectedVariant -cne 'prism-ml/bonsai-27b@q1_0'){throw 'Original Bonsai/context required; no substitution'}
$taskServer=(& 'C:/Users/yoiri/.lmstudio/bin/lms.exe' server status --json)|ConvertFrom-Json
if(!$taskServer.running -or $taskServer.port -ne 15630){throw 'Original endpoint15630 required'}
. (Join-Path $taskRoot 'artifacts/assert-cad-native-consumers-idle-2.ps1')
. (Join-Path $taskRoot 'artifacts/a5-native-binding-helper-1.ps1')
$taskUnrelated=@(Assert-CadNativeConsumersIdle)
$taskOutput=Join-Path $taskStage ('provider-'+$Scenario+'-'+$Iteration)
$taskExit=$taskOutput+'-exit.json';$taskBinding=$taskOutput+'-loaded-native.json'
if((Test-Path $taskOutput)-or(Test-Path $taskExit)){throw 'Preserve immutable original provider iteration'}
$taskInfo=[Diagnostics.ProcessStartInfo]::new((Get-Command dotnet).Source)
$taskInfo.WorkingDirectory=$taskProof.provider;$taskInfo.UseShellExecute=$false;$taskInfo.CreateNoWindow=$true;$taskInfo.WindowStyle='Hidden'
$taskInfo.RedirectStandardOutput=$true;$taskInfo.RedirectStandardError=$true
foreach($taskArg in @((Join-Path $taskProof.provider 'CadAgentWorkflowProbe.dll'),'--provider','lmstudio','--endpoint','http://127.0.0.1:15630/v1','--model','prism-ml/bonsai-27b','--scenario',$Scenario,'--output',$taskOutput)){$taskInfo.ArgumentList.Add($taskArg)}
$taskProcess=[Diagnostics.Process]::Start($taskInfo)
$taskStarted=[DateTimeOffset]::UtcNow
@{started=$taskStarted.ToString('o');pid=$taskProcess.Id;scenario=$Scenario;qualification=@{path=$taskQualificationPath;sha256=(Get-FileHash $taskQualificationPath).Hash};candidate=$taskProof.provider;output=$taskOutput;originalContext=8192;originalToolRounds=16;originalPerTurnMinutes=4;unrelatedProcessesPreserved=$taskUnrelated}|ConvertTo-Json -Depth 6|Set-Content ($taskOutput+'-start.json') -Encoding utf8
$taskOut=[IO.File]::Open($taskOutput+'.log',[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
$taskErr=[IO.File]::Open($taskOutput+'-stderr.log',[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
$taskCopy=$taskProcess.StandardOutput.BaseStream.CopyToAsync($taskOut);$taskErrorCopy=$taskProcess.StandardError.BaseStream.CopyToAsync($taskErr)
Write-Output ('Original Bonsai '+$Scenario+' raw-response replay PID='+$taskProcess.Id+'; original8192/16/4min retained; no desktop input')
try{
 while(!$taskProcess.WaitForExit(100)){
  if(!(Test-Path $taskBinding)){$null=Save-A5NativeBinding -ProcessId $taskProcess.Id -NativeDirectory (Join-Path $taskProof.provider 'occt') -Expected $taskProof.native62 -OutputPath $taskBinding}
 }
 $taskCode=$taskProcess.ExitCode
}finally{
 $null=$taskCopy.GetAwaiter().GetResult();$null=$taskErrorCopy.GetAwaiter().GetResult();$taskOut.Dispose();$taskErr.Dispose();$taskProcess.Dispose()
}
@{started=$taskStarted.ToString('o');completed=[DateTimeOffset]::UtcNow.ToString('o');exitCode=$taskCode;binding=$taskBinding;scenario=$Scenario;candidate=$taskProof.provider;qualification=@{path=$taskQualificationPath;sha256=(Get-FileHash $taskQualificationPath).Hash};actualModel='prism-ml/bonsai-27b';contextLength=8192;maxToolRounds=16;originalPerTurnMinutes=4;desktopInputStarted=$false;AFComplete=$false}|ConvertTo-Json -Depth 5|Set-Content $taskExit -Encoding utf8
Get-Content ($taskOutput+'.log') -Tail 4
if(!(Test-Path $taskBinding)){throw 'Actual native62 was not observed'}
exit $taskCode
