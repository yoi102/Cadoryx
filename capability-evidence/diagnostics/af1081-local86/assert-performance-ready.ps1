$ErrorActionPreference='Stop'
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx'
$taskDraft=Join-Path $taskRoot 'artifacts/af1081-local86-1'
$taskAll=Get-Content (Join-Path $taskDraft 'performance-freeze.json') -Raw|ConvertFrom-Json
if(!(Test-Path (Join-Path $taskRoot 'artifacts/local-sdk86-received-1/native-lane-released.json'))){throw 'SDK native lane not handed back'}
if($taskAll.state -cne 'LOCAL86_SOURCE1081_PERFORMANCE_PREPARED_NOT_RUNTIME'){throw 'Final1081 application required'}
foreach($taskRow in $taskAll.sourcePayload){if((Get-FileHash (Join-Path $taskDraft ('workspace/'+$taskRow.path))).Hash -cne $taskRow.sha256){throw 'Final candidate source differs'}}
$taskFull=Get-Content (Join-Path $taskRoot 'capability-evidence/diagnostics/af1081-local86/full-verification.json') -Raw|ConvertFrom-Json
if($taskFull.originalStrictSTEPGateChanged -or $taskFull.configurations.Count -ne 2 -or @($taskFull.configurations|Where-Object {$_.total -ne 6830 -or $_.failed -ne 24 -or $_.skipped -ne 0 -or $_.newFailureNames.Count -ne 0 -or !$_.actualNative62Matched}).Count -ne 0){throw 'Full frozen regression scope differs; inspect before performance use'}
$taskProvider=Get-Content (Join-Path $taskDraft 'provider-both-exit.json') -Raw|ConvertFrom-Json
if($taskProvider.scenarios.Count -ne 2){throw 'Both original model scenarios must actually finish before this native stage'}
$taskFreeze=@($taskAll.configurations|Where-Object configuration -CEQ 'Release')[0]
foreach($taskRow in $taskFreeze.payload){if((Get-FileHash (Join-Path $taskFreeze.candidate $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Final application payload differs'}}
if($taskAll.originalDirectRuns -ne 2 -or $taskAll.originalLifecycleCyclesPerRun -ne 20 -or $taskAll.targetMs -ne 6482 -or $taskAll.directTimeoutSeconds -ne 1800 -or $taskAll.productionOpenRuns -ne 2 -or $taskAll.productionOpenTimeoutSeconds -ne 900){throw 'Original performance budgets changed'}
if((Get-FileHash $taskAll.input.path).Hash -cne $taskAll.input.sha256 -or (Get-Item $taskAll.input.path).Length -ne 88258425){throw 'Original sample identity differs'}
if((Get-FileHash $taskAll.expectedImage.path).Hash -cne $taskAll.expectedImage.sha256 -or $taskAll.expectedImage.sha256 -cne '2E8022953A2D1D2118E6FBC4DDC8A964D7EEC1ACA8CA6B2AF055131595E63852'){throw 'Original full image reference differs'}
if((Get-FileHash $taskAll.originalInstrumentedVerifier.path).Hash -cne $taskAll.originalInstrumentedVerifier.sha256){throw 'Original production verifier differs'}
Write-Output 'Performance gate is independent: original24 STEP failures and model results remain unresolved unless their own receipts prove otherwise.'
