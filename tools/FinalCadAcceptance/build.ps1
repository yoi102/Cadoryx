[CmdletBinding()]
param([ValidateRange(1,100)][int]$Iteration=1,[ValidateRange(1,100)][int]$CandidateIteration=1)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskStage=Join-Path $taskRoot ('artifacts/af-final87-'+$CandidateIteration)
$taskPrepName=if($Iteration -eq 1){'preparation.json'}else{'preparation'+$Iteration+'.json'}
$taskPrep=Get-Content (Join-Path $taskStage $taskPrepName) -Raw|ConvertFrom-Json -DateKind String
$taskWork=$taskPrep.workspace;$taskCache=Join-Path $taskStage 'nuget-cache'
$taskSuffix=if($Iteration -eq 1){''}else{'-'+$Iteration}
if(Test-Path (Join-Path $taskStage ('build-exit'+$taskSuffix+'.json'))){throw 'Preserve immutable build evidence'}
$taskRows=@()
function Invoke-FinalStage([string]$Name,[string[]]$Arguments){
 $Name=$Name+$taskSuffix
 $taskLog=Join-Path $taskStage ($Name+'.log')
 if(Test-Path $taskLog){throw 'Preserve earlier stage log'}
 Write-Output ('Starting '+$Name)
 & dotnet @Arguments *> $taskLog
 $taskCode=$LASTEXITCODE
 $taskRow=@{stage=$Name;exitCode=$taskCode;log=$taskLog;completed=[DateTimeOffset]::UtcNow.ToString('o')}
 $script:taskRows+=@($taskRow)
 $taskRow|ConvertTo-Json|Set-Content (Join-Path $taskStage ($Name+'-exit.json'))
 Get-Content $taskLog -Tail 4
 if($taskCode -ne 0){throw ('Final application stage failed: '+$Name)}
}
Push-Location $taskWork
try{
 $taskProperties=@(('-p:RestorePackagesPath='+$taskCache),('-p:RawProgramSource='+$taskPrep.rawProgramSource),('-p:SourceRevisionId='+$taskPrep.baseHead))
 if($Iteration -eq 1){Invoke-FinalStage 'restore-update' (@('restore','CandidateAllProjects.slnx','--force-evaluate','--configfile','NuGet.Config','-v:minimal')+$taskProperties)}
 Invoke-FinalStage 'restore-locked' (@('restore','CandidateAllProjects.slnx','--locked-mode','--configfile','NuGet.Config','-v:minimal')+$taskProperties)
 foreach($taskConfig in @('Release','Debug')){
  Invoke-FinalStage ('core-build-'+$taskConfig.ToLower()) (@('build','Cadoryx.slnx','-c',$taskConfig,'--no-restore','-v:minimal')+$taskProperties)
  $taskFrozen=Join-Path $taskWork ('Cadoryx.wpf/bin/'+$taskConfig+'/net10.0-windows/win-x64')
  Invoke-FinalStage ('build-'+$taskConfig.ToLower()) (@('build','CandidateAllProjects.slnx','-c',$taskConfig,'--no-restore','-v:minimal',('-p:FrozenCadoryx='+$taskFrozen))+$taskProperties)
  Invoke-FinalStage ('discovery-'+$taskConfig.ToLower()) @('vstest',(Join-Path $taskWork ('Cadoryx.Tests/bin/'+$taskConfig+'/net10.0/Cadoryx.Tests.dll')),'/ListTests')
 }
 @{stages=$taskRows;preparation=$taskPrepName;productionApplied=$false;fullTestsStarted=$false;AFComplete=$false}|ConvertTo-Json -Depth 7|Set-Content (Join-Path $taskStage ('build-exit'+$taskSuffix+'.json'))
}finally{Pop-Location}
