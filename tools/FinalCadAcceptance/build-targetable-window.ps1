[CmdletBinding()]
param([ValidateRange(1,100)][int]$CandidateIteration=3)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskStage=Join-Path $taskRoot ('artifacts/af-final87-'+$CandidateIteration)
$taskPython='C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe'
& $taskPython (Join-Path $PSScriptRoot 'require-followup-runtime.py') $CandidateIteration
if($LASTEXITCODE -ne 0){throw 'Actual terminal qualification required'}
& $taskPython (Join-Path $PSScriptRoot 'targetable-window-host.py') prepare $CandidateIteration
if($LASTEXITCODE -ne 0){throw 'Isolated host preparation failed'}
$taskFreeze=Get-Content (Join-Path $taskStage 'freeze.json') -Raw|ConvertFrom-Json -DateKind String
$taskRows=@()
foreach($taskConfig in $taskFreeze.configurations){
 $taskOutput=Join-Path $taskStage ('hosts/d6-targetable-'+$taskConfig.configuration.ToLower()+'-1')
 $taskLog=Join-Path $taskStage ('targetable-window-'+$taskConfig.configuration.ToLower()+'.log')
 if((Test-Path $taskOutput)-or(Test-Path $taskLog)){throw 'Preserve original targetable host build'}
 & dotnet publish (Join-Path $taskStage 'targetable-window-host1/WindowHost.csproj') -c $taskConfig.configuration -o $taskOutput ('-p:CandidatePayload='+$taskConfig.candidate) ('-p:RestorePackagesPath='+(Join-Path $taskStage 'nuget-cache')) *> $taskLog
 $taskCode=$LASTEXITCODE
 $taskRows+=@(@{configuration=$taskConfig.configuration;candidate=$taskConfig.candidate;output=$taskOutput;log=$taskLog;exitCode=$taskCode})
 if($taskCode -ne 0){throw 'Targetable modal host build failed'}
}
@{stages=$taskRows;productChanged=$false;desktopInputStarted=$false}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $taskStage 'targetable-window-host1-build.json')
& $taskPython (Join-Path $PSScriptRoot 'targetable-window-host.py') audit $CandidateIteration
if($LASTEXITCODE -ne 0){throw 'Targetable modal payload qualification failed'}
