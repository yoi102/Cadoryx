[CmdletBinding()]
param([Parameter(Mandatory)][ValidateRange(0,35)][int]$CaseIndex,
      [ValidateRange(1,100)][int]$CandidateIteration=3,[ValidateRange(1,100)][int]$Iteration=1)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskDraft=Join-Path $taskRoot ('artifacts/af-final87-'+$CandidateIteration)
. (Join-Path $taskRoot 'tools/CurrentCadCandidate/require-current-return.ps1')
Assert-CurrentSdkNativeReturn -TaskRoot $taskRoot
& 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' (Join-Path $PSScriptRoot 'require-followup-runtime.py') $CandidateIteration
if($LASTEXITCODE -ne 0){throw 'Actual full/provider/recovery terminal audits required before D6 desktop input'}
$taskFreeze=Get-Content (Join-Path $taskDraft 'freeze.json') -Raw|ConvertFrom-Json -DateKind String
$taskPrep=Get-Content (Join-Path $taskDraft 'followup-preparation.json') -Raw|ConvertFrom-Json -DateKind String
if((Get-FileHash (Join-Path $taskDraft 'freeze.json')).Hash -cne $taskPrep.freeze.sha256 -or $taskPrep.D6Matrix.Count -ne 36){throw 'Original D6 matrix belongs to a different source freeze'}
$taskCase=$taskPrep.D6Matrix[$CaseIndex]
if($Iteration -ne 1){
 if(!$taskCase.output.EndsWith('-1',[StringComparison]::Ordinal)){throw 'Immutable case naming differs'}
 $taskCase.output=$taskCase.output.Substring(0,$taskCase.output.Length-1)+$Iteration
}
$taskHostProofPath=Join-Path $taskDraft 'hosts-verification.json'
$taskHostProof=Get-Content $taskHostProofPath -Raw|ConvertFrom-Json -DateKind String
if($taskHostProof.freeze.sha256 -cne $taskPrep.freeze.sha256 -or $taskHostProof.readinessSeconds -ne 30 -or $taskHostProof.D6DeadlineSeconds -ne 300){throw 'Original D6 host/budgets changed'}
$taskMatches=@($taskHostProof.hosts|Where-Object {$_.label -ceq 'd6-window36' -and $_.configuration -ceq 'Release'})
if($taskMatches.Count -ne 1){throw 'Exactly one current Release D6 host required'}
$taskHost=$taskMatches[0]
foreach($taskRow in $taskHost.payload){if((Get-FileHash (Join-Path $taskHost.output $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Window payload drift'}}
foreach($taskRow in $taskPrep.hostSources){if((Get-FileHash (Join-Path $taskRoot $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Observer source drift'}}
if((Get-FileHash $taskCase.fixture.path).Hash -cne $taskCase.fixture.sha256){throw 'Original fixture drift'}
$taskOutput=[IO.Path]::GetFullPath($taskCase.output)
$taskRelative=[IO.Path]::GetRelativePath((Join-Path $taskDraft 'windows'),$taskOutput)
if([IO.Path]::GetDirectoryName($taskRelative) -or !$taskRelative.StartsWith('window-',[StringComparison]::Ordinal)){throw 'Case output outside original matrix'}
if((Test-Path $taskOutput)-or(Test-Path ($taskOutput+'-exit.json'))-or(Test-Path ($taskOutput+'-start.json'))){throw 'Preserve immutable case output'}
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
