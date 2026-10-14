[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('recovery24','main-status36')][string]$Label,
      [ValidateRange(1,100)][int]$CandidateIteration=3,[ValidateRange(1,100)][int]$Iteration=1)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskDraft=Join-Path $taskRoot ('artifacts/af-final87-'+$CandidateIteration)
. (Join-Path $taskRoot 'tools/CurrentCadCandidate/require-current-return.ps1')
Assert-CurrentSdkNativeReturn -TaskRoot $taskRoot
& 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' (Join-Path $PSScriptRoot 'audit-full.py') $CandidateIteration
if($LASTEXITCODE -ne 0){throw 'Both complete current nondeferred results required'}
& 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' (Join-Path $PSScriptRoot 'audit-hosts.py') $CandidateIteration
if($LASTEXITCODE -ne 0){throw 'Six exact application hosts must be qualified'}
$taskFreeze=Get-Content (Join-Path $taskDraft 'freeze.json') -Raw|ConvertFrom-Json -DateKind String
$taskProof=Get-Content (Join-Path $taskDraft 'hosts-verification.json') -Raw|ConvertFrom-Json -DateKind String
if((Get-FileHash (Join-Path $taskDraft 'freeze.json')).Hash -cne $taskProof.freeze.sha256){throw 'Host freeze drift'}
$taskMatches=@($taskProof.hosts|Where-Object {$_.label -ceq $Label -and $_.configuration -ceq 'Release'})
if($taskMatches.Count -ne 1){throw 'Exactly one qualified recovery host required'}
$taskFixture=$taskMatches[0]
$taskApp=@($taskFreeze.configurations|Where-Object configuration -CEQ 'Release')[0]
foreach($taskRow in $taskFreeze.candidateSource){if((Get-FileHash (Join-Path $taskFreeze.workspace $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Frozen source drift'}}
foreach($taskRow in $taskApp.appPayload){if((Get-FileHash (Join-Path $taskApp.candidate $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Frozen app drift'}}
foreach($taskRow in $taskFixture.payload){if((Get-FileHash (Join-Path $taskFixture.output $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Frozen host drift'}}
. (Join-Path $taskRoot 'artifacts/assert-cad-native-consumers-idle-2.ps1')
$taskUnrelated=@(Assert-CadNativeConsumersIdle)
. (Join-Path $taskRoot 'artifacts/a5-native-binding-helper-1.ps1')
$taskFolder=Join-Path $taskDraft $Label
$taskRuntime='runtime-'+$Iteration
$taskOutput=Join-Path $taskFolder $taskRuntime
$taskReceipt=Join-Path $taskFolder ($taskRuntime+'-exit.json')
$taskBinding=Join-Path $taskFolder ($taskRuntime+'-loaded-native.json')
if((Test-Path $taskOutput)-or(Test-Path $taskReceipt)){throw 'Immutable recovery runtime exists'}
[IO.Directory]::CreateDirectory($taskFolder)>$null
if($Label -ceq 'main-status36'){[IO.Directory]::CreateDirectory($taskOutput)>$null}
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
$taskOut=[IO.File]::Open((Join-Path $taskFolder ($taskRuntime+'.log')),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
$taskErr=[IO.File]::Open((Join-Path $taskFolder ($taskRuntime+'-stderr.log')),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
$taskCopy=$taskProcess.StandardOutput.BaseStream.CopyToAsync($taskOut);$taskErrorCopy=$taskProcess.StandardError.BaseStream.CopyToAsync($taskErr)
$taskCount=if($Label -ceq 'recovery24'){24}else{36}
$taskEntry=[ordered]@{started=[DateTimeOffset]::UtcNow.ToString('o');state='RUNNING';pid=$taskProcess.Id;coordinatorPid=$PID;label=$Label;planned=$taskCount;candidate=$taskCandidate;output=$taskOutput;native=$taskBinding;unrelatedProcessesPreserved=$taskUnrelated;desktopInputStarted=$false;productionApplied=$false;publiclyPublished=$false;readinessSecondsEach=30;observationDeadlineSeconds=900}
$taskEntry|ConvertTo-Json -Depth 6|Set-Content (Join-Path $taskFolder ($taskRuntime+'-start.json'))
Write-Output ('Current final87 '+$Label+' started PID='+$taskProcess.Id+'; owned WPF windows, no mouse/keyboard injection')
$taskWatch=[Diagnostics.Stopwatch]::StartNew()
try {
 while(!$taskProcess.WaitForExit(100)){
  if(!(Test-Path $taskBinding)){$null=Save-A5NativeBinding -ProcessId $taskProcess.Id -NativeDirectory (Join-Path $taskCandidate 'occt') -Expected $taskFreeze.native62 -OutputPath $taskBinding}
  if($taskWatch.Elapsed.TotalSeconds -ge 900){throw 'Original900s observation deadline exceeded; preserve original30s readiness'}
 }
 $taskEntry.exitCode=$taskProcess.ExitCode
 if($taskProcess.ExitCode -ne 0){throw 'Recovery process failed; preserve result and original case conditions'}
 if(!(Test-Path $taskBinding)){throw 'Actual native62 observation missing'}
 $taskResult=Get-Content (Join-Path $taskOutput 'result.json') -Raw|ConvertFrom-Json -DateKind String
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
