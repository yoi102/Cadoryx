[CmdletBinding()]
param([ValidateSet('recovery24','main-status36')][string]$Label)
$ErrorActionPreference='Stop'
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx'
$taskDraft=Join-Path $taskRoot 'artifacts/af1081-local86-1'
$taskProof=Get-Content (Join-Path $taskRoot 'capability-evidence/diagnostics/af1081-local86/recovery-build-verification.json') -Raw|ConvertFrom-Json
$taskFreeze=Get-Content (Join-Path $taskDraft 'freeze.json') -Raw|ConvertFrom-Json
$taskFull=Get-Content (Join-Path $taskRoot 'capability-evidence/diagnostics/af1081-local86/full-verification.json') -Raw|ConvertFrom-Json
if($taskFull.originalStrictSTEPGateChanged -or $taskFull.configurations.Count -ne 2 -or @($taskFull.configurations|Where-Object {$_.total -ne 6830 -or $_.failed -ne 24 -or $_.skipped -ne 0 -or $_.newFailureNames.Count -ne 0 -or !$_.actualNative62Matched}).Count -ne 0){throw 'Inspect full scope or failures before recovery runtime'}
$taskFixture=@($taskProof.configurations|Where-Object {$_.label -ceq $Label -and $_.configuration -ceq 'Release'})[0]
if(!$taskFixture){throw 'Frozen recovery fixture required'}
$taskApp=@($taskFreeze.configurations|Where-Object configuration -CEQ 'Release')[0]
foreach($taskRow in $taskFreeze.sourcePayload){if((Get-FileHash (Join-Path $taskFreeze.workspace $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Frozen current source differs'}}
foreach($taskRow in $taskApp.appPayload){if((Get-FileHash (Join-Path $taskApp.candidate $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Frozen current app differs'}}
foreach($taskRow in $taskFixture.payload){if((Get-FileHash (Join-Path $taskFixture.output $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Frozen recovery fixture differs'}}
. (Join-Path $taskRoot 'artifacts/assert-cad-native-consumers-idle-2.ps1')
$taskUnrelated=@(Assert-CadNativeConsumersIdle)
. (Join-Path $taskRoot 'artifacts/a5-native-binding-helper-1.ps1')
$taskFolder=Join-Path $taskDraft $Label
$taskOutput=Join-Path $taskFolder 'runtime-1'
$taskReceipt=Join-Path $taskFolder 'runtime-1-exit.json'
$taskBinding=Join-Path $taskFolder 'runtime-1-loaded-native.json'
if((Test-Path $taskOutput)-or(Test-Path $taskReceipt)){throw 'Immutable recovery runtime exists'}
[IO.Directory]::CreateDirectory($taskOutput)>$null
$taskCandidate=if($Label -ceq 'recovery24'){$taskFixture.output}else{$taskApp.candidate}
$taskExecutable=if($Label -ceq 'recovery24'){'WpfAutoRecoveryConsumer.exe'}else{'Cadoryx.wpf.exe'}
$taskInfo=[Diagnostics.ProcessStartInfo]::new((Join-Path $taskCandidate $taskExecutable))
$taskInfo.WorkingDirectory=$taskCandidate;$taskInfo.UseShellExecute=$false;$taskInfo.CreateNoWindow=$true;$taskInfo.WindowStyle='Hidden'
$taskInfo.RedirectStandardOutput=$true;$taskInfo.RedirectStandardError=$true
$taskInfo.Environment['PATH']="$env:WINDIR\System32;$env:WINDIR;$env:ProgramFiles\dotnet"
foreach($taskKey in @($taskInfo.Environment.Keys)){if($taskKey -match '^(CSF_|CASROOT|OCCT|OCCTSHARP)' -or $taskKey -eq 'DOTNET_STARTUP_HOOKS'){$null=$taskInfo.Environment.Remove($taskKey)}}
if($Label -ceq 'main-status36'){
 $taskInfo.Environment['DOTNET_STARTUP_HOOKS']=Join-Path $taskFixture.output 'MainStatusRecovery.dll'
 $taskInfo.Environment['CADORYX_ASSET_STORE']='memory'
 $taskInfo.ArgumentList.Add('--a5-recovery-status-smoke')
}
$taskInfo.ArgumentList.Add($taskOutput)
$taskProcess=[Diagnostics.Process]::Start($taskInfo)
$taskOut=[IO.File]::Open((Join-Path $taskFolder 'runtime-1.log'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
$taskErr=[IO.File]::Open((Join-Path $taskFolder 'runtime-1-stderr.log'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
$taskCopy=$taskProcess.StandardOutput.BaseStream.CopyToAsync($taskOut);$taskErrorCopy=$taskProcess.StandardError.BaseStream.CopyToAsync($taskErr)
$taskCount=if($Label -ceq 'recovery24'){24}else{36}
$taskEntry=[ordered]@{started=[DateTimeOffset]::UtcNow.ToString('o');state='RUNNING';pid=$taskProcess.Id;coordinatorPid=$PID;label=$Label;planned=$taskCount;candidate=$taskCandidate;output=$taskOutput;native=$taskBinding;unrelatedProcessesPreserved=$taskUnrelated;desktopInputStarted=$false;productionApplied=$false;publiclyPublished=$false;readinessSecondsEach=30;observationDeadlineSeconds=900}
$taskEntry|ConvertTo-Json -Depth 6|Set-Content (Join-Path $taskFolder 'runtime-1-start.json')
Write-Output ('Current1081/local86 '+$Label+' started PID='+$taskProcess.Id+'; owned WPF windows, no mouse/keyboard injection')
$taskWatch=[Diagnostics.Stopwatch]::StartNew()
try {
 while(!$taskProcess.WaitForExit(100)){
  if(!(Test-Path $taskBinding)){$null=Save-A5NativeBinding -ProcessId $taskProcess.Id -NativeDirectory (Join-Path $taskCandidate 'occt') -Expected $taskFreeze.native62 -OutputPath $taskBinding}
  if($taskWatch.Elapsed.TotalSeconds -ge 900){throw 'Original900s observation deadline exceeded; preserve original30s readiness'}
 }
 $taskEntry.exitCode=$taskProcess.ExitCode
 if($taskProcess.ExitCode -ne 0){throw 'Recovery process failed; preserve result and original case conditions'}
 if(!(Test-Path $taskBinding)){throw 'Actual native62 observation missing'}
 $taskResult=Get-Content (Join-Path $taskOutput 'result.json') -Raw|ConvertFrom-Json
 $taskState=if($Label -ceq 'recovery24'){'PASS_OWNED_WPF_AUTO_RECOVERY24_PENDING_INDEPENDENT_AUDIT'}else{'PASS_ACTUAL_MAIN_STATUS_RECOVERY36_PENDING_INDEPENDENT_AUDIT'}
 if($taskResult.state -cne $taskState -or $taskResult.cases.Count -ne $taskCount){throw 'Complete original recovery matrix required'}
 $taskEntry.state='PROCESS_PASS_PENDING_INDEPENDENT_AUDIT_AND_IMAGE_REVIEW'
}catch{$taskEntry.state='FAIL';$taskEntry.error=$_.Exception.Message;throw}
finally {
 if(!$taskProcess.HasExited){$taskProcess.Kill();$taskProcess.WaitForExit()}
 $null=$taskCopy.GetAwaiter().GetResult();$null=$taskErrorCopy.GetAwaiter().GetResult();$taskOut.Dispose();$taskErr.Dispose();$taskProcess.Dispose()
 $taskEntry.completed=[DateTimeOffset]::UtcNow.ToString('o');$taskEntry.elapsedMs=$taskWatch.Elapsed.TotalMilliseconds
 $taskEntry|ConvertTo-Json -Depth 8|Set-Content $taskReceipt
}
