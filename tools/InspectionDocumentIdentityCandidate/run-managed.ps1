param([int]$RunOrdinal=3)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskDraft=Join-Path $taskRoot 'artifacts/inspection-document-id-draft-1'
$taskParent=Get-Content (Join-Path $taskRoot 'artifacts/af1082-d7-local87cu1-1/freeze.json') -Raw|ConvertFrom-Json -DateKind String
$taskParentWorkspace=[string]$taskParent.workspace
$taskProject=Join-Path $PSScriptRoot 'ManagedFeedbackProbe.csproj'
foreach($taskMode in @('baseline','candidate')){
 foreach($taskConfig in @('Release','Debug')){
  $taskOriginal=@($taskParent.configurations|Where-Object configuration -EQ $taskConfig)[0].testDirectory
  $taskViewModel=if($taskMode -eq 'baseline'){Join-Path $taskOriginal 'Cadoryx.ViewModels.dll'}else{Join-Path $taskDraft ('workspace/Cadoryx.ViewModels/bin/'+$taskConfig+'/net10.0/Cadoryx.ViewModels.dll')}
  $taskLabel=$taskMode+'-'+$taskConfig.ToLower()+'-'+$RunOrdinal
  $taskOut=Join-Path $taskDraft ('managed-'+$taskLabel)
  $taskLog=Join-Path $taskDraft ('build-managed-'+$taskLabel+'.log')
  if((Test-Path $taskOut)-or(Test-Path $taskLog)){throw 'Preserve existing managed evidence'}
  & dotnet build $taskProject -c $taskConfig -p:CandidateTests=$taskOriginal -p:CandidateViewModels=$taskViewModel -p:CandidateSource=$taskParentWorkspace -p:OutputPath=$taskOut -p:AppendTargetFrameworkToOutputPath=false -p:AppendRuntimeIdentifierToOutputPath=false -v:minimal *> $taskLog
  $taskCode=$LASTEXITCODE
  @{exitCode=$taskCode;mode=$taskMode;configuration=$taskConfig;viewModels=$taskViewModel;viewModelsSha256=(Get-FileHash $taskViewModel).Hash;output=$taskOut;nativeRuntimeStarted=$false}|ConvertTo-Json|Set-Content (Join-Path $taskDraft ('build-managed-'+$taskLabel+'-exit.json'))
  Get-Content $taskLog -Tail 6
  if($taskCode -ne 0){throw 'Managed assertion build failed'}
  $taskReceipt=Join-Path $taskDraft ('managed-'+$taskLabel+'-results.json')
  & dotnet (Join-Path $taskOut 'Cadoryx.Tests.dll') $taskReceipt *> (Join-Path $taskDraft ('managed-'+$taskLabel+'.log'))
  $taskCode=$LASTEXITCODE
  @{exitCode=$taskCode;mode=$taskMode;configuration=$taskConfig;receipt=$taskReceipt;nativeRuntimeStarted=$false}|ConvertTo-Json|Set-Content (Join-Path $taskDraft ('managed-'+$taskLabel+'-exit.json'))
  Get-Content (Join-Path $taskDraft ('managed-'+$taskLabel+'.log')) -Tail 3
 }
}
