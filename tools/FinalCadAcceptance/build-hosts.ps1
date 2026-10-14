[CmdletBinding()]
param([ValidateRange(1,100)][int]$CandidateIteration=1)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'));$taskStage=Join-Path $taskRoot ('artifacts/af-final87-'+$CandidateIteration)
& 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' (Join-Path $PSScriptRoot 'audit-full.py') $CandidateIteration
if($LASTEXITCODE -ne 0){throw 'Both complete nondeferred frozen results required before host build'}
$taskFreeze=Get-Content (Join-Path $taskStage 'freeze.json') -Raw|ConvertFrom-Json -DateKind String
$taskPrep=Get-Content (Join-Path $taskStage 'followup-preparation.json') -Raw|ConvertFrom-Json -DateKind String
$taskBaseName=if($CandidateIteration -eq 1){'preparation2.json'}else{'preparation.json'}
$taskBase=Get-Content (Join-Path $taskStage $taskBaseName) -Raw|ConvertFrom-Json -DateKind String
if(Test-Path (Join-Path $taskStage 'hosts-build-exit.json')){throw 'Preserve immutable hosts build'}
foreach($taskFile in $taskPrep.hostSources){if((Get-FileHash (Join-Path $taskRoot $taskFile.path)).Hash -cne $taskFile.sha256){throw 'Original host source drift'}}
$taskRows=@()
foreach($taskConfig in $taskFreeze.configurations){
 foreach($taskHost in $taskPrep.hostProjects){
  $taskLabel=$taskHost[0];$taskProject=Join-Path $taskFreeze.workspace $taskHost[1]
  $taskOutput=Join-Path $taskStage ('hosts/'+$taskLabel+'-'+$taskConfig.configuration.ToLower()+'-1')
  $taskLog=Join-Path $taskStage ('host-'+$taskLabel+'-'+$taskConfig.configuration.ToLower()+'.log')
  if((Test-Path $taskOutput)-or(Test-Path $taskLog)){throw 'Preserve earlier host iteration'}
  Write-Output ('Building '+$taskLabel+' '+$taskConfig.configuration+' against exact final87 application')
  & dotnet publish $taskProject -c $taskConfig.configuration -o $taskOutput ('-p:CandidatePayload='+$taskConfig.candidate) ('-p:RestorePackagesPath='+(Join-Path $taskStage 'nuget-cache')) ('-p:SourceRevisionId='+$taskBase.baseHead) *> $taskLog
  $taskCode=$LASTEXITCODE
  $taskRow=@{label=$taskLabel;configuration=$taskConfig.configuration;candidate=$taskConfig.candidate;output=$taskOutput;exitCode=$taskCode;log=$taskLog}
  $taskRow|ConvertTo-Json|Set-Content ($taskLog+'-exit.json');$taskRows+=@($taskRow)
  if($taskCode -ne 0){throw 'Host build failed; retain original observer and assertions'}
 }
}
@{stages=$taskRows;runtimeStarted=$false;desktopInputStarted=$false;AFComplete=$false}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $taskStage 'hosts-build-exit.json')
& 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' (Join-Path $PSScriptRoot 'audit-hosts.py') $CandidateIteration
if($LASTEXITCODE -ne 0){throw 'Host payload/source qualification failed'}
