[CmdletBinding()]
param([Parameter(Mandatory)][ValidateRange(0,35)][int]$CaseIndex,
      [switch]$UseUiCulturePatch,
      [ValidateRange(1,100)][int]$Iteration=1)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskDraft=Join-Path $taskRoot 'artifacts/af1082-f5-d7-local87cu1-1'
. (Join-Path $PSScriptRoot 'require-current-return.ps1')
Assert-CurrentSdkNativeReturn -TaskRoot $taskRoot
& 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' -X utf8 (Join-Path $PSScriptRoot 'require-full-gate.py')
if($LASTEXITCODE -ne 0){throw 'Both current full7117 actual results required before desktop acceptance'}
$taskFreeze=Get-Content (Join-Path $taskDraft 'freeze.json') -Raw|ConvertFrom-Json -DateKind String
$taskPrep=Get-Content (Join-Path $taskDraft 'followup-preparation.json') -Raw|ConvertFrom-Json -DateKind String
if((Get-FileHash (Join-Path $taskDraft 'freeze.json')).Hash -cne $taskPrep.freeze.sha256){throw 'Window preparation belongs to a different source freeze'}
$taskQueuePath=Join-Path $taskDraft 'followup-queue-exit.json'
if(!(Test-Path $taskQueuePath)){throw 'Current recovery/provider queue still holds this native lane; wait for its actual terminal result'}
$taskQueue=Get-Content $taskQueuePath -Raw|ConvertFrom-Json -DateKind String
if($UseUiCulturePatch){
 & 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' -X utf8 (Join-Path $PSScriptRoot 'require-followup-evidence.py')
 if($LASTEXITCODE -ne 0){throw 'Actual audited resumed recovery/provider terminal results required'}
}elseif($taskQueue.state -cne 'FOLLOWUPS_COMPLETED_IMAGE_REVIEW_AND_DESKTOP_PERFORMANCE_REMAIN'){throw 'Inspect the recovery/provider queue failure before interactive window acceptance'}
if($taskPrep.D6Matrix.Count -ne 36){throw 'Original36 matrix required'}
$taskCase=$taskPrep.D6Matrix[$CaseIndex]
$taskOriginalCaseOutput=$taskCase.output
if($Iteration -ne 1){
 if(!$taskCase.output.EndsWith('-1',[StringComparison]::Ordinal)){throw 'Original immutable output naming differs'}
 $taskCase.output=$taskCase.output.Substring(0,$taskCase.output.Length-1)+$Iteration
}
$taskHostProofPath=Join-Path $taskRoot $taskPrep.recoveryHosts.path
if((Get-FileHash $taskHostProofPath).Hash -cne $taskPrep.recoveryHosts.sha256){throw 'Current host qualification changed'}
$taskHostProof=Get-Content $taskHostProofPath -Raw|ConvertFrom-Json -DateKind String
if($taskHostProof.state -cne 'CURRENT_F5_D7_BOTH_RECOVERY24_MAIN36_D6WINDOW36_HOSTS_PUBLISH0_NOT_RUNTIME' -or (Get-FileHash (Join-Path $taskDraft 'freeze.json')).Hash -cne $taskHostProof.freeze.sha256){throw 'Host is not tied to this current application'}
if($taskHostProof.D6ReadinessSeconds -ne 30 -or $taskHostProof.D6ObservationSeconds -ne 300){throw 'Original D6 budgets changed'}
foreach($taskRow in $taskHostProof.windowObserverSources){if((Get-FileHash (Join-Path $taskRoot $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Original observer source changed'}}
$taskHost=@($taskHostProof.configurations|Where-Object {$_.label -ceq 'd6-window36' -and $_.configuration -ceq 'Release'})
if($taskHost.Count -ne 1){throw 'Exactly one current Release host required'}
$taskHost=$taskHost[0]
foreach($taskRow in $taskHost.payload){if((Get-FileHash (Join-Path $taskHost.output $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Frozen window/worker/resource payload changed'}}
if($UseUiCulturePatch){
 $taskFollowers=Get-Content (Join-Path $taskRoot 'artifacts/af1082-f5-d7-ui-culture-1/followups-verification1.json') -Raw|ConvertFrom-Json -DateKind String
 $taskHost=@($taskFollowers.followers|Where-Object label -CEQ 'd6-window36')[0]
 foreach($taskRow in $taskHost.payload){if((Get-FileHash (Join-Path $taskHost.output $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Qualified final UI window payload changed'}}
}
if((Get-FileHash $taskCase.fixture.path).Hash -cne $taskCase.fixture.sha256){throw 'Original exact fixture changed'}
$taskOutput=[IO.Path]::GetFullPath($taskCase.output)
if(![IO.Path]::GetRelativePath((Join-Path $taskDraft 'windows'),$taskOutput).StartsWith('window-')){throw 'Case output is outside the current planned matrix'}
if((Test-Path $taskOutput)-or(Test-Path ($taskOutput+'-exit.json'))-or(Test-Path ($taskOutput+'-start.json'))){throw 'Preserve immutable case and inspect its actual outcome'}
. (Join-Path $taskRoot 'artifacts/assert-cad-native-consumers-idle-2.ps1')
$taskUnrelated=@(Assert-CadNativeConsumersIdle)
. (Join-Path $taskRoot 'artifacts/a5-native-binding-helper-1.ps1')
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskOutput))>$null
$taskInfo=[Diagnostics.ProcessStartInfo]::new((Join-Path $taskHost.output 'WindowHost.exe'))
$taskInfo.WorkingDirectory=$taskHost.output;$taskInfo.UseShellExecute=$false;$taskInfo.CreateNoWindow=$true
# This owned test editor needs to be visible for the already authorized interaction.
$taskInfo.WindowStyle='Normal';$taskInfo.RedirectStandardOutput=$true;$taskInfo.RedirectStandardError=$true
$taskInfo.Environment['PATH']="$env:WINDIR\System32;$env:WINDIR;$env:ProgramFiles\dotnet"
foreach($taskKey in @($taskInfo.Environment.Keys)){if($taskKey -match '^(CSF_|CASROOT|OCCT|OCCTSHARP)' -or $taskKey -eq 'DOTNET_STARTUP_HOOKS'){$null=$taskInfo.Environment.Remove($taskKey)}}
foreach($taskArg in @('--d6-newbody-ui-smoke',$taskOutput,$taskCase.fixture.path,$taskCase.language,$taskCase.route,$taskCase.phase)){$taskInfo.ArgumentList.Add($taskArg)}
$taskProcess=[Diagnostics.Process]::Start($taskInfo)
$taskEntry=[ordered]@{startedUtc=[DateTimeOffset]::UtcNow.ToString('o');state='OWNED_D6_WINDOW_RUNNING';caseIndex=$CaseIndex;case=$taskCase;pid=$taskProcess.Id;processStartUtc=$taskProcess.StartTime.ToUniversalTime().ToString('o');coordinatorPid=$PID;candidate=$taskHost.output;output=$taskOutput;readinessSeconds=30;observationSeconds=300;hostInjectsInput=$false;productionApplied=$false;AFComplete=$false;unrelatedProcessesPreserved=$taskUnrelated}
$taskEntry|ConvertTo-Json -Depth 8|Set-Content ($taskOutput+'-start.json')
Write-Output ('Owned D6 '+$taskCase.phase+'/'+$taskCase.language+'/'+$taskCase.label+'/'+$taskCase.route+' PID='+$taskProcess.Id+'; select a returned modal with Computer Use; original30/300s retained.')
$taskOut=[IO.File]::Open(($taskOutput+'.log'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
$taskErr=[IO.File]::Open(($taskOutput+'-stderr.log'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
$taskCopy=$taskProcess.StandardOutput.BaseStream.CopyToAsync($taskOut);$taskErrorCopy=$taskProcess.StandardError.BaseStream.CopyToAsync($taskErr)
$taskBinding=$taskOutput+'-loaded-native.json'
try {
 while(!$taskProcess.WaitForExit(100)){
  if(!(Test-Path $taskBinding)){$null=Save-A5NativeBinding -ProcessId $taskProcess.Id -NativeDirectory (Join-Path $taskHost.output 'occt') -Expected $taskFreeze.native62 -OutputPath $taskBinding}
 }
 $taskEntry.exitCode=$taskProcess.ExitCode
 if($taskProcess.ExitCode -ne 0){throw 'Owned D6 observer failed; preserve exact fixture, control trace and original assertions'}
 if(!(Test-Path $taskBinding)-or!(Test-Path (Join-Path $taskOutput 'result.json'))){throw 'D6 result or actual native62 missing'}
 $taskEntry.state='PROCESS_EXIT0_PENDING_INDEPENDENT_TRACE_AUDIT_AND_DESKTOP_REVIEW'
} catch {$taskEntry.state='FAIL';$taskEntry.error=$_.Exception.Message;throw}
finally {
 if(!$taskProcess.HasExited){$taskProcess.Kill();$taskProcess.WaitForExit()}
 $null=$taskCopy.GetAwaiter().GetResult();$null=$taskErrorCopy.GetAwaiter().GetResult()
 $taskOut.Dispose();$taskErr.Dispose();$taskProcess.Dispose()
 $taskEntry.completedUtc=[DateTimeOffset]::UtcNow.ToString('o')
 $taskEntry|ConvertTo-Json -Depth 8|Set-Content ($taskOutput+'-exit.json')
}
