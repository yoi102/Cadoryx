[CmdletBinding()]
param([Parameter(Mandatory)][int]$FullCoordinatorPid,[ValidateRange(1,100)][int]$CandidateIteration=1)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'));$taskStage=Join-Path $taskRoot ('artifacts/af-final87-'+$CandidateIteration)
$taskFull=Get-CimInstance Win32_Process -Filter ('ProcessId='+$FullCoordinatorPid)
if(!$taskFull -or $taskFull.CommandLine -notmatch 'FinalCadAcceptance.run-full.ps1'){throw 'Specific original full-suite coordinator is not live; inspect its result instead of restarting'}
if($taskFull.CommandLine -notmatch ('-CandidateIteration\s+'+$CandidateIteration+'(?:\s|$)')){throw 'Coordinator belongs to another candidate'}
if(Test-Path (Join-Path $taskStage 'after-full-start.json')){throw 'Preserve original queued follow-up'}
@{started=[DateTimeOffset]::UtcNow.ToString('o');pid=$PID;fullCoordinatorPid=$FullCoordinatorPid;fullCreationDate=$taskFull.CreationDate;scope='Wait exact existing full run; audit both, build immutable hosts, replay original drawing/workspace; no keyboard/mouse';AFComplete=$false}|ConvertTo-Json -Depth 4|Set-Content (Join-Path $taskStage 'after-full-start.json')
Write-Output ('Waiting existing full coordinator PID='+$FullCoordinatorPid+'; original cases and budgets retained')
while($true){
 $taskObserved=Get-CimInstance Win32_Process -Filter ('ProcessId='+$FullCoordinatorPid)
 if(!$taskObserved -or $taskObserved.CreationDate -ne $taskFull.CreationDate){break}
 Start-Sleep -Milliseconds 1000
}
& 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' (Join-Path $PSScriptRoot 'audit-full.py') $CandidateIteration
if($LASTEXITCODE -ne 0){throw 'Original full run incomplete or failed; no model/window follow-up started'}
& (Join-Path $PSScriptRoot 'build-hosts.ps1') -CandidateIteration $CandidateIteration
if($LASTEXITCODE -ne 0){throw 'Host build failure; original files preserved'}
& 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' (Join-Path $PSScriptRoot 'audit-hosts.py') $CandidateIteration
if($LASTEXITCODE -ne 0){throw 'Host payload/source qualification failed before any model replay'}
$taskRows=@()
foreach($taskScenario in @('drawing','workspace')){
 & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'run-provider.ps1') -Scenario $taskScenario -Iteration 1 -CandidateIteration $CandidateIteration
 $taskCode=$LASTEXITCODE
 $taskTerminal=Join-Path $taskStage ('provider-'+$taskScenario+'-1-exit.json')
 if(!(Test-Path $taskTerminal)){throw 'Provider has no actual terminal receipt; do not restart'}
 $taskRows+=@(@{scenario=$taskScenario;exitCode=$taskCode;terminal=$taskTerminal;sha256=(Get-FileHash $taskTerminal).Hash})
}
@{scenarios=$taskRows;modelsTerminal=$true;recoveryRuntimeStarted=$false;desktopInputStarted=$false;performanceStarted=$false;AFComplete=$false}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $taskStage 'after-full-exit.json')
