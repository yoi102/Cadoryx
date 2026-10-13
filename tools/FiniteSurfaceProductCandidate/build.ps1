$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskDraft=Join-Path $taskRoot 'artifacts/af1082-d7-local87cu1-1'
$taskWork=Join-Path $taskDraft 'workspace'
$taskCache=Join-Path $taskDraft 'nuget-cache'
$taskStages=@()
if(Test-Path (Join-Path $taskDraft 'build-exit.json')){throw 'Preserve completed build evidence'}
function Invoke-CandidateStage([string]$taskStage,[string[]]$taskArguments){
 $taskLog=Join-Path $taskDraft ($taskStage+'.log')
 if(Test-Path $taskLog){throw ('Preserve prior log: '+$taskStage)}
 Write-Output ('Starting '+$taskStage)
 & dotnet @taskArguments 2>&1|Tee-Object -FilePath $taskLog
 $taskCode=$LASTEXITCODE
 $taskRow=@{stage=$taskStage;exitCode=$taskCode;log=$taskLog;completed=[DateTimeOffset]::UtcNow.ToString('o')}
 $taskRow|ConvertTo-Json|Set-Content (Join-Path $taskDraft ($taskStage+'-exit.json'))
 $script:taskStages+=@($taskRow)
 if($taskCode -ne 0){throw ('Candidate stage failed: '+$taskStage)}
}
Push-Location $taskWork
try{
 Invoke-CandidateStage 'restore-update' @('restore','CandidateAllProjects.slnx','--force-evaluate','--configfile','NuGet.Config',('-p:RestorePackagesPath='+$taskCache),'-v:minimal')
 Invoke-CandidateStage 'restore-locked' @('restore','CandidateAllProjects.slnx','--locked-mode','--configfile','NuGet.Config',('-p:RestorePackagesPath='+$taskCache),'-v:minimal')
 & 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' -X utf8 (Join-Path $PSScriptRoot 'prepare.py') locks
 if($LASTEXITCODE -ne 0){throw 'Exact lock audit failed'}
 foreach($taskConfig in @('Release','Debug')){
  Invoke-CandidateStage $taskConfig.ToLower() @('build','Cadoryx.slnx','-c',$taskConfig,'--no-restore',('-p:RestorePackagesPath='+$taskCache),'-v:minimal')
 }
 Invoke-CandidateStage 'provider-publish' @('publish','tools/CadAgentWorkflowProbe/CadAgentWorkflowProbe.csproj','-c','Release','--no-restore','-o',(Join-Path $taskDraft 'provider'),('-p:RestorePackagesPath='+$taskCache),'-v:minimal')
 foreach($taskConfig in @('Release','Debug')){
  Invoke-CandidateStage ('discovery-'+$taskConfig.ToLower()) @('vstest',(Join-Path $taskWork ('Cadoryx.Tests/bin/'+$taskConfig+'/net10.0/Cadoryx.Tests.dll')),'/ListTests')
 }
 @{stages=$taskStages;productionApplied=$false;publiclyPublished=$false;actualFullTestRun=$false}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $taskDraft 'build-exit.json')
 & 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' -X utf8 (Join-Path $PSScriptRoot 'prepare.py') freeze
 if($LASTEXITCODE -ne 0){throw 'Final application/source/payload audit failed'}
}finally{Pop-Location}
