$ErrorActionPreference='Stop'
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx'
$taskDraft=Join-Path $taskRoot 'artifacts/af1082-f5-d7-local87cu1-1'
$taskAll=Get-Content (Join-Path $taskDraft 'performance-freeze.json') -Raw|ConvertFrom-Json -DateKind String
. (Join-Path $PSScriptRoot 'require-current-return.ps1')
Assert-CurrentSdkNativeReturn -TaskRoot $taskRoot
if($taskAll.state -cne 'CURRENT_F5_D7_EXACT87CU1_PERFORMANCE_PREPARED_NOT_RUNTIME'){throw 'CurrentF5D7 application required'}
foreach($taskRow in $taskAll.sourcePayload){if((Get-FileHash (Join-Path $taskDraft ('workspace/'+$taskRow.path))).Hash -cne $taskRow.sha256){throw 'Final candidate source differs'}}
& 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' -X utf8 (Join-Path $PSScriptRoot 'require-full-gate.py')
if($LASTEXITCODE -ne 0){throw 'Current full7117 actual evidence gate failed'}
& 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' -X utf8 (Join-Path $PSScriptRoot 'require-provider-evidence.py')
if($LASTEXITCODE -ne 0){throw 'Both original provider outcomes and current provenance must be actually verified'}
$taskPausePath=Join-Path $taskDraft 'sdk-performance-pause.json'
if(!(Test-Path $taskPausePath)){throw 'SDK CPU builds must be explicitly paused before MiniPC sampling; a returned native lane does not prove CPU pause'}
$taskPause=Get-Content $taskPausePath -Raw|ConvertFrom-Json -DateKind String
if(!$taskPause.sdkCpuBuildsExplicitlyPaused -or !$taskPause.rootMayRunOriginalMiniPC -or $taskPause.sdkThread -cne '01a0f7b3-ff5f-7cb0-bff9-a5caad1a49b0' -or $taskPause.acceptedFrozenPackageVersion -cne '8.0.1-preview.87.cu1' -or $taskPause.performanceFreezeSHA256 -cne (Get-FileHash (Join-Path $taskDraft 'performance-freeze.json')).Hash){throw 'Current SDK CPU pause does not cover this exact performance candidate'}
if((Get-FileHash $taskPause.sdkConfirmation.path).Hash -cne $taskPause.sdkConfirmation.sha256){throw 'SDK explicit CPU pause source receipt changed'}
$taskSdkPause=Get-Content $taskPause.sdkConfirmation.path -Raw|ConvertFrom-Json -DateKind String
if($taskSdkPause.state -cne 'SDK_CPU_WORK_QUIESCENT_NATIVE_LANE_REMAINS_CADORYX' -or $taskSdkPause.activeOwnNativeJobs -ne 0 -or $taskSdkPause.activeOwnBuildToolSessions -ne 0 -or $taskSdkPause.currentLaneOwner -cne 'Cadoryx'){throw 'SDK receipt no longer states the required CPU pause'}
$taskFreeze=@($taskAll.configurations|Where-Object configuration -CEQ 'Release')[0]
foreach($taskRow in $taskFreeze.payload){if((Get-FileHash (Join-Path $taskFreeze.candidate $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Final application payload differs'}}
if($taskAll.originalDirectRuns -ne 2 -or $taskAll.originalLifecycleCyclesPerRun -ne 20 -or $taskAll.targetMs -ne 6482 -or $taskAll.directTimeoutSeconds -ne 1800 -or $taskAll.productionOpenRuns -ne 2 -or $taskAll.productionOpenTimeoutSeconds -ne 900){throw 'Original performance budgets changed'}
if((Get-FileHash $taskAll.input.path).Hash -cne $taskAll.input.sha256 -or (Get-Item $taskAll.input.path).Length -ne 88258425){throw 'Original sample identity differs'}
if((Get-FileHash $taskAll.expectedImage.path).Hash -cne $taskAll.expectedImage.sha256 -or $taskAll.expectedImage.sha256 -cne '2E8022953A2D1D2118E6FBC4DDC8A964D7EEC1ACA8CA6B2AF055131595E63852'){throw 'Original full image reference differs'}
if((Get-FileHash $taskAll.originalInstrumentedVerifier.path).Hash -cne $taskAll.originalInstrumentedVerifier.sha256){throw 'Original production verifier differs'}
Write-Output 'Performance gate is independent: original24 STEP failures and model results remain unresolved unless their own receipts prove otherwise.'
if($taskUseUiCulturePatch){
 & 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' -X utf8 (Join-Path $PSScriptRoot 'require-followup-evidence.py')
 if($LASTEXITCODE -ne 0){throw 'Qualified final UI and actual resumed terminal followups required'}
 $taskUiFollowers=Get-Content (Join-Path $taskRoot 'artifacts/af1082-f5-d7-ui-culture-1/followups-verification1.json') -Raw|ConvertFrom-Json -DateKind String
 if($taskUiFollowers.originalPerformance.sha256 -cne (Get-FileHash (Join-Path $taskDraft 'performance-freeze.json')).Hash){throw 'UI qualification must inherit the exact original performance freeze covered by the SDK pause'}
 $taskFreeze=$taskUiFollowers.performance
 foreach($taskRow in $taskFreeze.payload){if((Get-FileHash (Join-Path $taskFreeze.candidate $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Qualified final UI performance payload differs'}}
}
