[CmdletBinding()]
param([ValidateRange(1,100)][int]$CandidateIteration=3)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskStage=Join-Path $taskRoot ('artifacts/af-final87-'+$CandidateIteration)
. (Join-Path $taskRoot 'tools/CurrentCadCandidate/require-current-return.ps1')
Assert-CurrentSdkNativeReturn -TaskRoot $taskRoot
foreach($taskAudit in @('require-followup-runtime.py','audit-material-build.py','audit-material82.py')){
 & 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' (Join-Path $PSScriptRoot $taskAudit) $CandidateIteration
 if($LASTEXITCODE -ne 0){throw 'Actual full/provider/recovery/material82 evidence required before manual material UI'}
}
$taskFreeze=Get-Content (Join-Path $taskStage 'freeze.json') -Raw|ConvertFrom-Json -DateKind String
$taskApp=@($taskFreeze.configurations|Where-Object configuration -CEQ 'Release')[0]
$taskProof=Get-Content (Join-Path $taskStage 'material-build-verification.json') -Raw|ConvertFrom-Json -DateKind String
$taskObserver=@($taskProof.builds|Where-Object label -CEQ 'material-observer')[0]
foreach($taskRow in $taskApp.appPayload){if((Get-FileHash (Join-Path $taskApp.candidate $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Frozen material UI app changed'}}
foreach($taskRow in $taskObserver.payload){if((Get-FileHash (Join-Path $taskObserver.output $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Exact observer payload changed'}}
. (Join-Path $taskRoot 'artifacts/assert-cad-native-consumers-idle-2.ps1')
$taskUnrelated=@(Assert-CadNativeConsumersIdle)
. (Join-Path $taskRoot 'artifacts/a5-native-binding-helper-1.ps1')
$taskOutput=Join-Path $taskStage 'material-desktop1';$taskReceipt=Join-Path $taskStage 'material-desktop1-exit.json'
$taskBinding=Join-Path $taskStage 'material-desktop1-loaded-native.json'
if((Test-Path $taskOutput)-or(Test-Path $taskReceipt)){throw 'Preserve immutable actual material UI attempt'}
[IO.Directory]::CreateDirectory($taskOutput)>$null
$taskInfo=[Diagnostics.ProcessStartInfo]::new((Join-Path $taskApp.candidate 'Cadoryx.wpf.exe'))
$taskInfo.WorkingDirectory=$taskApp.candidate;$taskInfo.UseShellExecute=$false;$taskInfo.CreateNoWindow=$true
# This is the explicitly authorized visible acceptance application.
$taskInfo.WindowStyle='Normal';$taskInfo.RedirectStandardOutput=$true;$taskInfo.RedirectStandardError=$true
foreach($taskKey in @($taskInfo.Environment.Keys)){if($taskKey -match '^(CSF_|CASROOT|OCCT|OCCTSHARP)' -or $taskKey -eq 'DOTNET_STARTUP_HOOKS'){$null=$taskInfo.Environment.Remove($taskKey)}}
$taskInfo.Environment['DOTNET_STARTUP_HOOKS']=Join-Path $taskObserver.output 'MaterialDesktopObserver.dll'
$taskInfo.ArgumentList.Add('--a5-material-desktop-smoke');$taskInfo.ArgumentList.Add($taskOutput)
$taskProcess=[Diagnostics.Process]::Start($taskInfo);$taskWatch=[Diagnostics.Stopwatch]::StartNew()
$taskEntry=@{started=[DateTimeOffset]::UtcNow.ToString('o');state='RUNNING_MANUAL_UI_PENDING_ACCEPTANCE';pid=$taskProcess.Id;coordinatorPid=$PID;processStartUtc=$taskProcess.StartTime.ToUniversalTime().ToString('o');output=$taskOutput;application=$taskApp.candidate;observer=Join-Path $taskObserver.output 'MaterialDesktopObserver.dll';freezeSHA256=(Get-FileHash (Join-Path $taskStage 'freeze.json')).Hash;originalReadySeconds=60;originalDeadlineMinutes=30;unrelatedPreserved=$taskUnrelated;productionApplied=$false;AFComplete=$false}
$taskEntry|ConvertTo-Json -Depth 5|Set-Content (Join-Path $taskStage 'material-desktop1-start.json')
$taskOut=[IO.File]::Open((Join-Path $taskStage 'material-desktop1.log'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
$taskErr=[IO.File]::Open((Join-Path $taskStage 'material-desktop1-stderr.log'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
$taskCopy=$taskProcess.StandardOutput.BaseStream.CopyToAsync($taskOut);$taskErrorCopy=$taskProcess.StandardError.BaseStream.CopyToAsync($taskErr)
Write-Output ('Owned material manual UI PID='+$taskProcess.Id+'; all picker/author actions require actual Computer Use input; observer does not operate controls')
try{
 while(!$taskProcess.WaitForExit(100)){
  if(!(Test-Path $taskBinding)){$null=Save-A5NativeBinding -ProcessId $taskProcess.Id -NativeDirectory (Join-Path $taskApp.candidate 'occt') -Expected $taskFreeze.native62 -OutputPath $taskBinding}
  if($taskWatch.Elapsed.TotalMinutes -ge 30){throw 'Original30 minute manual UI deadline exceeded; preserve all observations'}
 }
 $taskEntry.exitCode=$taskProcess.ExitCode
 if($taskEntry.exitCode -ne 0){throw 'Actual material UI process failed'}
 if(!(Test-Path $taskBinding)){throw 'Actual native62 binding missing'}
 if((Test-Path (Join-Path $taskOutput 'observer-failure.json')) -or @(Get-ChildItem $taskOutput -Filter 'observation-failure-*').Count){throw 'Observer failed; do not infer UI acceptance'}
 $taskEntry.state='PROCESS_EXIT0_OBSERVATIONS_RECORDED_PENDING_INDEPENDENT_MANUAL_UI_ASSERTIONS'
}catch{$taskEntry.state='FAIL';$taskEntry.error=$_.Exception.Message;throw}
finally{
 if(!$taskProcess.HasExited){$taskProcess.Kill();$taskProcess.WaitForExit()}
 $null=$taskCopy.GetAwaiter().GetResult();$null=$taskErrorCopy.GetAwaiter().GetResult();$taskOut.Dispose();$taskErr.Dispose();$taskProcess.Dispose()
 $taskEntry.elapsedMs=$taskWatch.Elapsed.TotalMilliseconds;$taskEntry.completed=[DateTimeOffset]::UtcNow.ToString('o');$taskEntry.actualNative=$taskBinding
 $taskEntry|ConvertTo-Json -Depth 6|Set-Content $taskReceipt
}
