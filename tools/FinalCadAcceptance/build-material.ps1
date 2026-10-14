[CmdletBinding()]
param([ValidateRange(1,100)][int]$CandidateIteration=3)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskStage=Join-Path $taskRoot ('artifacts/af-final87-'+$CandidateIteration)
$taskPrep=Get-Content (Join-Path $taskStage 'material-preparation.json') -Raw|ConvertFrom-Json -DateKind String
if((Get-FileHash (Join-Path $taskStage 'freeze.json')).Hash -cne $taskPrep.freeze.sha256 -or $taskPrep.originalPlanned -ne 82 -or $taskPrep.originalFrames -ne 346){throw 'Original material acceptance provenance required'}
foreach($taskRow in $taskPrep.sourcePayload){if((Get-FileHash (Join-Path $taskRoot $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Material consumer source changed'}}
foreach($taskRow in $taskPrep.observer){foreach($taskKey in @('original','current')){if((Get-FileHash (Join-Path $taskRoot $taskRow.$taskKey.path)).Hash -cne $taskRow.$taskKey.sha256){throw 'Original observer source changed'}}}
$taskBuildReceipt=Join-Path $taskStage 'material-build-exit.json'
if(Test-Path $taskBuildReceipt){throw 'Preserve immutable material build'}
$taskRows=@()
foreach($taskSpec in @(
 @('material82',(Join-Path $taskStage 'material82/MaterialAuthoringConsumer.csproj')),
 @('material-observer',(Join-Path $taskRoot 'tools/CadMaterialDesktopProbe/MaterialDesktopObserver.csproj')))){
 $taskLabel=$taskSpec[0];$taskOutput=Join-Path $taskStage ($taskLabel+'-publish1');$taskLog=Join-Path $taskStage ($taskLabel+'-build1.log')
 if((Test-Path $taskOutput)-or(Test-Path $taskLog)){throw 'Preserve earlier auxiliary build'}
 Write-Output ('Building '+$taskLabel+' only; no product rebuild, no native runtime, no UI input')
 & dotnet publish $taskSpec[1] -c Release --self-contained false -o $taskOutput ('-p:RestorePackagesPath='+(Join-Path $taskStage 'nuget-cache')) *> $taskLog
 $taskCode=$LASTEXITCODE
 $taskRow=@{label=$taskLabel;output=$taskOutput;log=$taskLog;exitCode=$taskCode;project=$taskSpec[1]};$taskRows+=@($taskRow)
 $taskRow|ConvertTo-Json|Set-Content ($taskLog+'-exit.json')
 if($taskCode -ne 0){throw 'Original auxiliary material source failed to build; preserve it and inspect'}
}
@{stages=$taskRows;runtimeStarted=$false;desktopInputStarted=$false;AFComplete=$false}|ConvertTo-Json -Depth 5|Set-Content $taskBuildReceipt
