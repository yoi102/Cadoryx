param([int]$RunOrdinal=1)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskDraft=Join-Path $taskRoot 'artifacts/inspection-document-id-root-1'
$taskBaseline=Get-Content (Join-Path $taskDraft 'baseline-viewmodels.json') -Raw|ConvertFrom-Json -DateKind String
foreach($taskMode in @('baseline','root')){
 foreach($taskConfig in @('Release','Debug')){
  $taskOriginal=Join-Path $taskRoot ('Cadoryx.Tests/bin/'+$taskConfig+'/net10.0')
  if($taskMode -eq 'baseline'){
   $taskCaptured=@($taskBaseline.configurations|Where-Object configuration -EQ $taskConfig)[0]
   $taskSavedDir=Join-Path $taskDraft ('baseline/'+$taskConfig)
   if(!(Test-Path $taskSavedDir)){New-Item -ItemType Directory -Path $taskSavedDir|Out-Null}
   $taskViewModel=Join-Path $taskSavedDir 'Cadoryx.ViewModels.dll'
   if(!(Test-Path $taskViewModel)){Copy-Item -LiteralPath $taskCaptured.path -Destination $taskViewModel}
   if((Get-FileHash $taskViewModel).Hash -cne $taskCaptured.sha256){throw 'Captured original public85 ViewModels changed'}
  }else{$taskViewModel=Join-Path $taskOriginal 'Cadoryx.ViewModels.dll'}
  $taskLabel=$taskMode+'-'+$taskConfig.ToLower()+'-'+$RunOrdinal
  $taskOut=Join-Path $taskDraft ('managed-'+$taskLabel)
  $taskLog=Join-Path $taskDraft ('build-managed-'+$taskLabel+'.log')
  if((Test-Path $taskOut)-or(Test-Path $taskLog)){throw 'Preserve original root evidence'}
  & dotnet build (Join-Path $PSScriptRoot 'ManagedFeedbackProbe.csproj') -c $taskConfig -p:CandidateTests=$taskOriginal -p:CandidateViewModels=$taskViewModel -p:CandidateSource=$taskRoot -p:OutputPath=$taskOut -p:AppendTargetFrameworkToOutputPath=false -p:AppendRuntimeIdentifierToOutputPath=false -v:minimal *> $taskLog
  $taskCode=$LASTEXITCODE
  @{exitCode=$taskCode;mode=$taskMode;configuration=$taskConfig;viewModels=$taskViewModel;viewModelsSha256=(Get-FileHash $taskViewModel).Hash;output=$taskOut;sdk='8.0.1-preview.85';nativeRuntimeStarted=$false}|ConvertTo-Json|Set-Content (Join-Path $taskDraft ('build-managed-'+$taskLabel+'-exit.json'))
  Get-Content $taskLog -Tail 6
  if($taskCode -ne 0){throw 'Root assertion compile failed'}
  $taskReceipt=Join-Path $taskDraft ('managed-'+$taskLabel+'-results.json')
  & dotnet (Join-Path $taskOut 'Cadoryx.Tests.dll') $taskReceipt *> (Join-Path $taskDraft ('managed-'+$taskLabel+'.log'))
  $taskCode=$LASTEXITCODE
  @{exitCode=$taskCode;mode=$taskMode;configuration=$taskConfig;receipt=$taskReceipt;nativeRuntimeStarted=$false}|ConvertTo-Json|Set-Content (Join-Path $taskDraft ('managed-'+$taskLabel+'-exit.json'))
  Get-Content (Join-Path $taskDraft ('managed-'+$taskLabel+'.log')) -Tail 3
 }
}
