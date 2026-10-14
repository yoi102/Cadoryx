[CmdletBinding()]
param([ValidateSet('drawing','workspace')][string]$Scenario,[ValidateRange(1,100)][int]$Iteration=1,[ValidateRange(1,100)][int]$CandidateIteration=1)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'));$taskStage=Join-Path $taskRoot ('artifacts/af-final87-'+$CandidateIteration)
. (Join-Path $taskRoot 'tools/CurrentCadCandidate/require-current-return.ps1');Assert-CurrentSdkNativeReturn -TaskRoot $taskRoot
& 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' (Join-Path $PSScriptRoot 'audit-full.py') $CandidateIteration
if($LASTEXITCODE -ne 0){throw 'Both actual nondeferred frozen results required before provider replay'}
$taskFreeze=Get-Content (Join-Path $taskStage 'freeze.json') -Raw|ConvertFrom-Json -DateKind String
$taskPrep=Get-Content (Join-Path $taskStage 'followup-preparation.json') -Raw|ConvertFrom-Json -DateKind String
if($taskPrep.freeze.sha256 -cne (Get-FileHash (Join-Path $taskStage 'freeze.json')).Hash){throw 'Follow-up source freeze changed'}
foreach($taskRow in $taskFreeze.providerPayload){if((Get-FileHash (Join-Path $taskFreeze.provider $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Provider payload drift'}}
foreach($taskRow in $taskPrep.models){if((Get-FileHash (Join-Path $taskRoot $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Original provider inputs drift'}}
$taskModels=@(((& 'C:/Users/yoiri/.lmstudio/bin/lms.exe' ps --json)|ConvertFrom-Json))
$taskSelected=@($taskModels|Where-Object identifier -CEQ $taskPrep.model)
if($taskSelected.Count -ne 1 -or $taskSelected[0].contextLength -ne 8192 -or $taskSelected[0].selectedVariant -cne $taskPrep.variant){throw 'Original actual model/context required'}
$taskServer=(& 'C:/Users/yoiri/.lmstudio/bin/lms.exe' server status --json)|ConvertFrom-Json
if(!$taskServer.running -or $taskServer.port -ne 15630){throw 'Original actual endpoint required'}
. (Join-Path $taskRoot 'artifacts/assert-cad-native-consumers-idle-2.ps1');$taskUnrelated=@(Assert-CadNativeConsumersIdle)
. (Join-Path $taskRoot 'artifacts/a5-native-binding-helper-1.ps1')
$taskOutput=Join-Path $taskStage ('provider-'+$Scenario+'-'+$Iteration);$taskBinding=$taskOutput+'-loaded-native.json'
if((Test-Path $taskOutput)-or(Test-Path ($taskOutput+'-exit.json'))){throw 'Preserve immutable provider iteration'}
$taskInfo=[Diagnostics.ProcessStartInfo]::new((Get-Command dotnet).Source)
$taskInfo.WorkingDirectory=$taskFreeze.provider;$taskInfo.UseShellExecute=$false;$taskInfo.CreateNoWindow=$true;$taskInfo.WindowStyle='Hidden'
$taskInfo.RedirectStandardOutput=$true;$taskInfo.RedirectStandardError=$true
foreach($taskArg in @((Join-Path $taskFreeze.provider 'CadAgentWorkflowProbe.dll'),'--provider','lmstudio','--endpoint',$taskPrep.endpoint,'--model',$taskPrep.model,'--scenario',$Scenario,'--output',$taskOutput)){$taskInfo.ArgumentList.Add($taskArg)}
$taskProcess=[Diagnostics.Process]::Start($taskInfo);$taskStarted=[DateTimeOffset]::UtcNow
@{started=$taskStarted.ToString('o');pid=$taskProcess.Id;candidate=$taskFreeze.provider;scenario=$Scenario;native62=$taskFreeze.native62;actualModel=$taskSelected[0];unrelatedPreserved=$taskUnrelated;freezeSHA256=(Get-FileHash (Join-Path $taskStage 'freeze.json')).Hash}|ConvertTo-Json -Depth 6|Set-Content ($taskOutput+'-start.json')
$taskOut=[IO.File]::Open($taskOutput+'.log',[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
$taskErr=[IO.File]::Open($taskOutput+'-stderr.log',[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
$taskCopy=$taskProcess.StandardOutput.BaseStream.CopyToAsync($taskOut);$taskErrorCopy=$taskProcess.StandardError.BaseStream.CopyToAsync($taskErr)
Write-Output ('Original final87 '+$Scenario+' replay PID='+$taskProcess.Id+'; original8192/16/4min; no desktop input')
try{while(!$taskProcess.WaitForExit(100)){if(!(Test-Path $taskBinding)){$null=Save-A5NativeBinding -ProcessId $taskProcess.Id -NativeDirectory (Join-Path $taskFreeze.provider 'occt') -Expected $taskFreeze.native62 -OutputPath $taskBinding}};$taskCode=$taskProcess.ExitCode}
finally{$null=$taskCopy.GetAwaiter().GetResult();$null=$taskErrorCopy.GetAwaiter().GetResult();$taskOut.Dispose();$taskErr.Dispose();$taskProcess.Dispose()}
@{started=$taskStarted.ToString('o');completed=[DateTimeOffset]::UtcNow.ToString('o');exitCode=$taskCode;scenario=$Scenario;candidate=$taskFreeze.provider;binding=$taskBinding;context=8192;toolRounds=16;perTurnMinutes=4;desktopInputStarted=$false;AFComplete=$false}|ConvertTo-Json -Depth 5|Set-Content ($taskOutput+'-exit.json')
Get-Content ($taskOutput+'.log') -Tail 4
if(!(Test-Path $taskBinding)){throw 'Actual provider62 binding missing'}
exit $taskCode
