[CmdletBinding()]
param([ValidateSet('recovery24','main-status36')][string]$Label,
      [ValidateRange(1,100)][int]$Iteration=1,
      [switch]$UseRevisedMainFixture,
      [switch]$UseUiCulturePatch,
      [ValidateRange(1,2)][int]$UiCultureRevision=1)
$ErrorActionPreference='Stop'
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx'
$taskDraft=Join-Path $taskRoot 'artifacts/af1082-f5-d7-local87cu1-1'
$taskProof=Get-Content (Join-Path $taskRoot 'capability-evidence/diagnostics/af1082-f5-d7-local87cu1/hosts-verification.json') -Raw|ConvertFrom-Json -DateKind String
$taskFreeze=Get-Content (Join-Path $taskDraft 'freeze.json') -Raw|ConvertFrom-Json -DateKind String
. (Join-Path $PSScriptRoot 'require-current-return.ps1')
Assert-CurrentSdkNativeReturn -TaskRoot $taskRoot
& 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' -X utf8 (Join-Path $PSScriptRoot 'require-full-gate.py')
if($LASTEXITCODE -ne 0){throw 'Current full7117 actual evidence gate failed'}
if($taskProof.state -cne 'CURRENT_F5_D7_BOTH_RECOVERY24_MAIN36_D6WINDOW36_HOSTS_PUBLISH0_NOT_RUNTIME' -or (Get-FileHash (Join-Path $taskDraft 'freeze.json')).Hash -cne $taskProof.freeze.sha256){throw 'Recovery host build differs from current application'}
$taskFixture=@($taskProof.configurations|Where-Object {$_.label -ceq $Label -and $_.configuration -ceq 'Release'})[0]
if(!$taskFixture){throw 'Frozen recovery fixture required'}
if($UseRevisedMainFixture){
 if($Label -cne 'main-status36'){throw 'Revised fixture only applies to the original main36 preparation'}
 $taskRevision=Get-Content (Join-Path $taskDraft 'main-status36-revision2-build-verification.json') -Raw|ConvertFrom-Json -DateKind String
 if($taskRevision.state -cne 'REVISED_MAIN36_PREPARATION_ONLY_CURRENT_F5_D7_BUILD0_NOT_RUNTIME' -or $taskRevision.freeze.sha256 -cne $taskProof.freeze.sha256 -or !$taskRevision.originalNativeMatrixAndAssertionsAnd30_900BudgetsRetained){throw 'Revised main fixture provenance or original conditions differ'}
 foreach($taskRow in @($taskRevision.originalSource)+@($taskRevision.revisedSources)){if((Get-FileHash (Join-Path $taskRoot $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Revised main source changed'}}
 $taskFixture=$taskRevision
}
$taskApp=@($taskFreeze.configurations|Where-Object configuration -CEQ 'Release')[0]
if($UseUiCulturePatch){
 if($Label -ceq 'main-status36' -and !$UseRevisedMainFixture){throw 'UI-only main36 culture delta requires the revised original main36 fixture'}
 $taskUiProof=if($UiCultureRevision -eq 1){'application-verification.json'}else{'application-verification2.json'}
 $taskUi=Get-Content (Join-Path $taskRoot ('artifacts/af1082-f5-d7-ui-culture-1/'+$taskUiProof)) -Raw|ConvertFrom-Json -DateKind String
 if($taskUi.state -cne 'CURRENT_UI_RECOVERY_CULTURE_ONLY_DELTA_BUILD0_ORIGINAL_CORE18_SDK13_NATIVE62_IDENTICAL' -or $taskUi.baseFreeze.sha256 -cne $taskProof.freeze.sha256 -or !$taskUi.core18Sdk13Native62AndWorkerBytesUnchanged){throw 'Current UI delta qualification required'}
 foreach($taskReference in @($taskUi.baseFullBoth7117,$taskUi.sourceChange,$taskUi.compileLog)){if((Get-FileHash (Join-Path $taskRoot $taskReference.path)).Hash -cne $taskReference.sha256){throw 'UI delta source or original full gate changed'}}
 $taskApp=@{candidate=$taskUi.candidate;appPayload=$taskUi.payload}
 if($Label -ceq 'recovery24'){
  if($UiCultureRevision -ne 2){throw 'Original24 requires qualified final UI revision2'}
  $taskFollowers=Get-Content (Join-Path $taskRoot 'artifacts/af1082-f5-d7-ui-culture-1/followups-verification1.json') -Raw|ConvertFrom-Json -DateKind String
  if($taskFollowers.state -cne 'QUALIFIED_WPF_CULTURE_ONLY_FOLLOWERS_ORIGINAL_CORE_AND_ASSERTIONS_RETAINED_NOT_RUNTIME' -or !$taskFollowers.originalRecoveryAndWindowHostAssertionsUnchanged){throw 'Qualified unchanged original24 follower required'}
  foreach($taskReference in @($taskFollowers.application,$taskFollowers.base,$taskFollowers.main36,$taskFollowers.main36DirectReview,$taskFollowers.originalHosts,$taskFollowers.source)){
   if((Get-FileHash (Join-Path $taskRoot $taskReference.path)).Hash -cne $taskReference.sha256){throw 'UI original24 qualification reference differs'}
  }
  $taskFixture=@($taskFollowers.followers|Where-Object label -CEQ 'recovery24')[0]
 }
}
foreach($taskRow in $taskFreeze.sourcePayload){if((Get-FileHash (Join-Path $taskFreeze.workspace $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Frozen current source differs'}}
foreach($taskRow in $taskApp.appPayload){if((Get-FileHash (Join-Path $taskApp.candidate $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Frozen current app differs'}}
foreach($taskRow in $taskFixture.payload){if((Get-FileHash (Join-Path $taskFixture.output $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Frozen recovery fixture differs'}}
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
Write-Output ('CurrentF5D7/local87cu1 '+$Label+' started PID='+$taskProcess.Id+'; owned WPF windows, no mouse/keyboard injection')
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
