$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskDraft=Join-Path $taskRoot ('artifacts/af-final87-'+$CandidateIteration)
. (Join-Path $taskRoot 'tools/CurrentCadCandidate/require-current-return.ps1')
Assert-CurrentSdkNativeReturn -TaskRoot $taskRoot
foreach($taskAudit in @('audit-full.py','audit-provider.py')){
 & 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' (Join-Path $PSScriptRoot $taskAudit) $CandidateIteration
 if($LASTEXITCODE -ne 0){throw 'Actual complete regression and both provider terminal outcomes required before performance'}
}
$taskParent=Get-Content (Join-Path $taskDraft 'freeze.json') -Raw|ConvertFrom-Json -DateKind String
$taskPrep=Get-Content (Join-Path $taskDraft 'followup-preparation.json') -Raw|ConvertFrom-Json -DateKind String
if((Get-FileHash (Join-Path $taskDraft 'freeze.json')).Hash -cne $taskPrep.freeze.sha256){throw 'Performance preparation freeze drift'}
$taskAll=$taskPrep.performance
if($taskAll.originalDirectRuns -ne 2 -or $taskAll.originalLifecycleCyclesPerRun -ne 20 -or $taskAll.targetMs -ne 6482 -or $taskAll.directTimeoutSeconds -ne 1800 -or $taskAll.productionOpenRuns -ne 2 -or $taskAll.productionOpenTimeoutSeconds -ne 900){throw 'Original performance conditions changed'}
if((Get-FileHash $taskAll.input.path).Hash -cne $taskAll.input.sha256 -or (Get-Item $taskAll.input.path).Length -ne 88258425){throw 'Original MiniPC sample changed'}
if((Get-FileHash $taskAll.expectedImage.path).Hash -cne '2E8022953A2D1D2118E6FBC4DDC8A964D7EEC1ACA8CA6B2AF055131595E63852'){throw 'Original complete PNG changed'}
if((Get-FileHash $taskAll.originalInstrumentedVerifier.path).Hash -cne $taskAll.originalInstrumentedVerifier.sha256){throw 'Original production-open verifier changed'}
$taskConfig=@($taskParent.configurations|Where-Object configuration -CEQ 'Release')[0]
foreach($taskRow in $taskParent.candidateSource){if((Get-FileHash (Join-Path $taskParent.workspace $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Frozen application source drift'}}
foreach($taskRow in $taskConfig.appPayload){if((Get-FileHash (Join-Path $taskConfig.candidate $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Frozen performance payload drift'}}
$taskFreeze=@{candidate=$taskConfig.candidate;payload=$taskConfig.appPayload;native=$taskParent.native62}
$taskReturnPath=Join-Path $taskRoot 'artifacts/af1082-f5-d7-root-integration-1/sdk-final-da-return-received1.json'
$taskReturn=Get-Content $taskReturnPath -Raw|ConvertFrom-Json -DateKind String
$taskSdkReturn=Get-Content $taskReturn.sdkConfirmation.path -Raw|ConvertFrom-Json -DateKind String
if(!$taskSdkReturn.explicitlyReturned -or !$taskSdkReturn.rootMayResumeNativeWindowModelPerformance -or $taskSdkReturn.relatedFullCpuJobs -ne 0 -or $taskSdkReturn.relatedNativeWindowJobs -ne 0 -or $taskSdkReturn.finalPackageVersion -cne $taskParent.version){throw 'Current final87 explicit CPU/performance return required'}
$taskSdkRoot=[IO.Path]::GetFullPath((Join-Path $taskRoot '../OcctSharp/OcctSharp'))
$taskCpuJobs=@(Get-CimInstance Win32_Process|Where-Object {$_.Name -match '^(dotnet|MSBuild|cmake|ninja|cl|link|csc|vstest.console|testhost)\.exe$' -and $_.CommandLine -and $_.CommandLine.IndexOf($taskSdkRoot,[StringComparison]::OrdinalIgnoreCase) -ge 0})
if($taskCpuJobs.Count){throw 'SDK build/test CPU work is active; preserve it and do not sample performance'}
. (Join-Path $taskRoot 'artifacts/assert-cad-native-consumers-idle-2.ps1')
$null=Assert-CadNativeConsumersIdle
$taskCpuObservation=Join-Path $taskDraft ('performance-'+$taskPerformanceLabel+'-lane1.json')
if(Test-Path $taskCpuObservation){throw 'Preserve immutable performance lane observation'}
@{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');sdkReturnSHA256=(Get-FileHash $taskReturn.sdkConfirmation.path).Hash;sdkCpuJobsObserved=$taskCpuJobs.Count;freezeSHA256=(Get-FileHash (Join-Path $taskDraft 'freeze.json')).Hash;actualPerformancePass=$false;AFComplete=$false}|ConvertTo-Json|Set-Content $taskCpuObservation
Write-Output 'Final87 original performance conditions verified; original STEP24 and model outcomes remain separate.'
