$ErrorActionPreference='Stop'
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx'
$taskDraft=Join-Path $taskRoot 'artifacts/af1081-local86-1'
$taskProof=Get-Content (Join-Path $taskDraft 'freeze.json') -Raw|ConvertFrom-Json
$taskResume=Get-Content (Join-Path $taskDraft 'debug-native-resume.json') -Raw|ConvertFrom-Json
if(!$taskResume.sdkExplicitHandoffReceived -or !$taskResume.actualCadConsumersIdle -or $taskResume.candidatePackage -cne $taskProof.version){throw 'Required actual SDK native hand-back not received'}
$taskReleaseAudit=Get-Content (Join-Path $taskRoot 'capability-evidence/diagnostics/af1081-local86/release-full-verification.json') -Raw|ConvertFrom-Json
$taskRelease=@($taskReleaseAudit.configurations)[0]
if($taskRelease.total -ne 6830 -or $taskRelease.passed -ne 6806 -or $taskRelease.failed -ne 24 -or $taskRelease.skipped -ne 0 -or $taskRelease.newFailureNames.Count -ne 0 -or !$taskRelease.actualNative62Matched){throw 'Original completed Release must remain independently verified'}
$taskOld=Get-Content (Join-Path $taskDraft 'debug-interrupted-observation-1.json') -Raw|ConvertFrom-Json
if($taskOld.debugExitExists -or $taskOld.debugTrxExists -or $taskOld.observedRecordedProcesses.Count -ne 0){throw 'Do not restart a live or completed original job'}
$taskConfig=@($taskProof.configurations|Where-Object configuration -CEQ 'Debug')[0]
. (Join-Path $taskRoot 'artifacts/assert-cad-native-consumers-idle-2.ps1')
$taskIdle=@(Assert-CadNativeConsumersIdle)
. (Join-Path $taskRoot 'artifacts/a5-native-binding-helper-1.ps1')
foreach($taskRow in $taskProof.sourcePayload){if((Get-FileHash (Join-Path $taskProof.workspace $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Original frozen source differs'}}
foreach($taskRow in $taskConfig.payload){if((Get-FileHash (Join-Path $taskConfig.testDirectory $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Original Debug binary differs'}}
$taskOutput=Join-Path $taskDraft 'full6830-debug-2'
$taskTerminal=Join-Path $taskDraft 'full-both-exit.json'
if((Test-Path $taskOutput)-or(Test-Path $taskTerminal)){throw 'Immutable resumed Debug or terminal exists'}
New-Item -ItemType Directory -Path $taskOutput|Out-Null
$taskInfo=[Diagnostics.ProcessStartInfo]::new((Get-Command dotnet).Source)
$taskInfo.WorkingDirectory=$taskProof.workspace;$taskInfo.UseShellExecute=$false;$taskInfo.CreateNoWindow=$true;$taskInfo.WindowStyle='Hidden'
$taskInfo.RedirectStandardOutput=$true;$taskInfo.RedirectStandardError=$true
foreach($taskArg in @('vstest',(Join-Path $taskConfig.testDirectory 'Cadoryx.Tests.dll'),('/TestCaseFilter:'+$taskProof.fullTestFilter),'/Logger:console;verbosity=normal','/Logger:trx;LogFileName=runtime.trx',('/ResultsDirectory:'+$taskOutput))){$taskInfo.ArgumentList.Add($taskArg)}
$taskStarted=[DateTimeOffset]::UtcNow;$taskProcess=[Diagnostics.Process]::Start($taskInfo)
@{started=$taskStarted.ToString('o');configuration='Debug';pid=$taskProcess.Id;coordinatorPid=$PID;testDirectory=$taskConfig.testDirectory;scope=6830;originalInterruptedRunPreserved=$true;releaseRerun=$false;unrelatedProcessesPreserved=$taskIdle}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $taskOutput 'start.json')
Write-Output ('Resumed original Debug6830 PID='+$taskProcess.Id+'; completed Release remains unchanged')
$taskLog=[IO.File]::Open((Join-Path $taskOutput 'stdout.log'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
$taskErr=[IO.File]::Open((Join-Path $taskOutput 'stderr.log'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
$taskCopy=$taskProcess.StandardOutput.BaseStream.CopyToAsync($taskLog);$taskErrorCopy=$taskProcess.StandardError.BaseStream.CopyToAsync($taskErr)
$taskBinding=Join-Path $taskOutput 'loaded-native.json'
try {
 while(!$taskProcess.WaitForExit(100)){
  if(!(Test-Path $taskBinding)){
   foreach($taskHost in @(Get-Process testhost -ErrorAction SilentlyContinue)){
    if($taskHost.Path -ceq (Join-Path $taskConfig.testDirectory 'testhost.exe')){$null=Save-A5NativeBinding -ProcessId $taskHost.Id -NativeDirectory (Join-Path $taskConfig.testDirectory 'occt') -Expected $taskProof.native62 -OutputPath $taskBinding}
   }
  }
 }
 $taskCode=$taskProcess.ExitCode
}finally{$null=$taskCopy.GetAwaiter().GetResult();$null=$taskErrorCopy.GetAwaiter().GetResult();$taskLog.Dispose();$taskErr.Dispose();$taskProcess.Dispose()}
$taskDebug=@{configuration='Debug';exitCode=$taskCode;started=$taskStarted.ToString('o');completed=[DateTimeOffset]::UtcNow.ToString('o');output=$taskOutput;bindingCaptured=(Test-Path $taskBinding);originalInterruptedRunPreserved=$true}
$taskDebug|ConvertTo-Json -Depth 5|Set-Content (Join-Path $taskOutput 'exit.json')
Get-Content (Join-Path $taskOutput 'stdout.log') -Tail 6
if(!(Test-Path $taskBinding)-or !(Test-Path (Join-Path $taskOutput 'runtime.trx'))){throw 'Resumed Debug lacks terminal scope or actual native binding'}
$taskReleaseExit=Get-Content (Join-Path $taskDraft 'full6830-release-1/exit.json') -Raw|ConvertFrom-Json
@{started=$taskReleaseExit.started;completed=[DateTimeOffset]::UtcNow.ToString('o');configurations=@($taskReleaseExit,$taskDebug);expected=6830;originalReleasePreserved=$true;resumedOnlyMissingDebug=$true;originalInterruptedDebugPreserved=$true;productionApplied=$false;publiclyPublished=$false;AFComplete=$false;G='USER_DEFERRED';cleanup=$false}|ConvertTo-Json -Depth 7|Set-Content $taskTerminal
& 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' -X utf8 (Join-Path $taskRoot 'capability-evidence/diagnostics/af1081-local86/audit-full.py')
if($LASTEXITCODE -ne 0){throw 'Resumed full terminal audit failed; preserve all original output'}
