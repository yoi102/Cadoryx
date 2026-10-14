[CmdletBinding()]
param([ValidateRange(1,100)][int]$CandidateIteration=3)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskStage=Join-Path $taskRoot ('artifacts/af-final87-'+$CandidateIteration)
. (Join-Path $taskRoot 'tools/CurrentCadCandidate/require-current-return.ps1')
Assert-CurrentSdkNativeReturn -TaskRoot $taskRoot
foreach($taskAudit in @('audit-full.py','audit-material-build.py')){
 & 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' (Join-Path $PSScriptRoot $taskAudit) $CandidateIteration
 if($LASTEXITCODE -ne 0){throw 'Full regression and exact original material build must qualify before runtime'}
}
$taskProof=Get-Content (Join-Path $taskStage 'material-build-verification.json') -Raw|ConvertFrom-Json -DateKind String
$taskHost=@($taskProof.builds|Where-Object label -CEQ 'material82')[0]
foreach($taskRow in $taskHost.payload){if((Get-FileHash (Join-Path $taskHost.output $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Material payload drift'}}
. (Join-Path $taskRoot 'artifacts/assert-cad-native-consumers-idle-2.ps1')
$taskUnrelated=@(Assert-CadNativeConsumersIdle)
. (Join-Path $taskRoot 'artifacts/a5-native-binding-helper-1.ps1')
$taskOutput=Join-Path $taskStage 'material82-results1';$taskReceipt=Join-Path $taskStage 'material82-runtime1-exit.json'
if((Test-Path $taskOutput)-or(Test-Path $taskReceipt)){throw 'Preserve immutable original82 runtime'}
$taskInfo=[Diagnostics.ProcessStartInfo]::new((Join-Path $taskHost.output 'MaterialAuthoringConsumer.exe'))
$taskInfo.WorkingDirectory=$taskHost.output;$taskInfo.UseShellExecute=$false;$taskInfo.CreateNoWindow=$true;$taskInfo.WindowStyle='Hidden'
$taskInfo.RedirectStandardOutput=$true;$taskInfo.RedirectStandardError=$true;$taskInfo.ArgumentList.Add($taskOutput)
$taskInfo.Environment['PATH']="$env:WINDIR\System32;$env:WINDIR"
foreach($taskKey in @($taskInfo.Environment.Keys)){if($taskKey -match '^(CSF_|CASROOT|OCCT|OCCTSHARP)' -or $taskKey -eq 'DOTNET_STARTUP_HOOKS'){$null=$taskInfo.Environment.Remove($taskKey)}}
$taskProcess=[Diagnostics.Process]::Start($taskInfo)
$taskBinding=Join-Path $taskStage 'material82-runtime1-loaded-native.json'
$taskEntry=@{started=[DateTimeOffset]::UtcNow.ToString('o');pid=$taskProcess.Id;coordinatorPid=$PID;state='RUNNING_ORIGINAL82';planned=82;publish=$taskHost.output;output=$taskOutput;binding=$taskBinding;unrelatedPreserved=$taskUnrelated;desktopInputStarted=$false;AFComplete=$false}
$taskEntry|ConvertTo-Json -Depth 5|Set-Content (Join-Path $taskStage 'material82-runtime1-start.json')
$taskOut=[IO.File]::Open((Join-Path $taskStage 'material82-runtime1.log'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
$taskErr=[IO.File]::Open((Join-Path $taskStage 'material82-runtime1-stderr.log'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
$taskCopy=$taskProcess.StandardOutput.BaseStream.CopyToAsync($taskOut);$taskErrorCopy=$taskProcess.StandardError.BaseStream.CopyToAsync($taskErr)
Write-Output ('Original82 final87 native consumer PID='+$taskProcess.Id+'; hidden owned viewports, no keyboard/mouse')
try{
 while(!$taskProcess.WaitForExit(100)){if(!(Test-Path $taskBinding)){$null=Save-A5NativeBinding -ProcessId $taskProcess.Id -NativeDirectory (Join-Path $taskHost.output 'occt') -Expected $taskProof.native62 -OutputPath $taskBinding}}
 $taskEntry.exitCode=$taskProcess.ExitCode
 if($taskEntry.exitCode -ne 0){throw 'Original82 runtime failed; retain actual failures and all cases'}
 if(!(Test-Path $taskBinding)){throw 'Actual62 binding missing'}
 $taskEntry.state='PROCESS_PASS_ORIGINAL82_PENDING_INDEPENDENT_PAYLOAD_AND_IMAGE_AUDIT'
}catch{$taskEntry.state='FAIL';$taskEntry.error=$_.Exception.Message;throw}
finally{
 if(!$taskProcess.HasExited){$taskProcess.Kill();$taskProcess.WaitForExit()}
 $null=$taskCopy.GetAwaiter().GetResult();$null=$taskErrorCopy.GetAwaiter().GetResult();$taskOut.Dispose();$taskErr.Dispose();$taskProcess.Dispose()
 $taskEntry.completed=[DateTimeOffset]::UtcNow.ToString('o');$taskEntry|ConvertTo-Json -Depth 6|Set-Content $taskReceipt
}
