$ErrorActionPreference='Stop'
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx'
$taskDraft=Join-Path $taskRoot 'artifacts/af1081-local86-1'
$taskResumePath=Join-Path $taskDraft 'debug-native-resume.json'
if(!(Test-Path $taskResumePath)){throw 'Wait for actual SDK hand-back; do not launch a competing consumer'}
$taskResume=Get-Content $taskResumePath -Raw|ConvertFrom-Json
if(!$taskResume.sdkExplicitHandoffReceived -or !$taskResume.actualCadConsumersIdle){throw 'Actual explicit native hand-back required'}
. (Join-Path $taskRoot 'artifacts/assert-cad-native-consumers-idle-2.ps1')
$taskIdle=@(Assert-CadNativeConsumersIdle)
$taskStdout=Join-Path $taskDraft 'debug-resume-coordinator-1.log'
$taskStderr=Join-Path $taskDraft 'debug-resume-coordinator-1-stderr.log'
$taskReceipt=Join-Path $taskDraft 'debug-resume-coordinator-1-start.json'
if((Test-Path $taskStdout)-or(Test-Path $taskStderr)-or(Test-Path $taskReceipt)){throw 'Immutable resume coordinator exists; inspect its actual process'}
$taskScript=Join-Path $taskRoot 'capability-evidence/diagnostics/af1081-local86/resume-debug.ps1'
$taskProcess=Start-Process -FilePath (Get-Command pwsh).Source -ArgumentList @('-NoProfile','-File',('"'+$taskScript+'"')) -WorkingDirectory $taskRoot -WindowStyle Hidden -RedirectStandardOutput $taskStdout -RedirectStandardError $taskStderr -PassThru
$taskProcess.Refresh()
@{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');pid=$taskProcess.Id;processStarted=$taskProcess.StartTime.ToUniversalTime().ToString('o');script=$taskScript;stdout=$taskStdout;stderr=$taskStderr;windowStyle='Hidden';releaseRerun=$false;scope='Original frozen Debug6830 only';unrelatedProcessesPreserved=$taskIdle;desktopInputStarted=$false;AFComplete=$false}|ConvertTo-Json -Depth 6|Set-Content $taskReceipt
Write-Output ('Detached Debug coordinator launched PID='+$taskProcess.Id+'; observe actual process and terminal files, not a prior tool handle')
