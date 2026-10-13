[CmdletBinding()]
param([ValidateSet(5,6)][int]$Revision=5)
$ErrorActionPreference='Stop'
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx'
$taskDraft=Join-Path $taskRoot 'artifacts/af1081-local86-1'
$taskEvidence=Join-Path $taskRoot 'capability-evidence/diagnostics/af1081-local86'
$taskHoldPath=Join-Path $taskDraft 'provider-sdk-transition-hold.json'
$taskObservationPath=Join-Path $taskDraft 'provider-sdk-transition-observation.json'
$taskStartPath=Join-Path $taskDraft $(if($Revision -eq 5){'far90-followup-1-start.json'}else{'far90-followup-'+$Revision+'-start.json'})
$taskGrantPath=Join-Path $taskDraft 'provider-sdk-transition-granted.json'
if(Test-Path $taskGrantPath){throw 'Preserve the existing SDK grant; inspect actual state'}
if(Test-Path (Join-Path $taskDraft 'provider-runtime-freeze.json')){throw 'Original provider has entered native launch; do not grant a competing slot'}
$taskHold=Get-Content $taskHoldPath -Raw|ConvertFrom-Json -DateKind String
if($taskHold.scope -cne 'SDK_CX_CY_NATIVE_QUALIFICATION_BEFORE_ORIGINAL_PROVIDER' -or $taskHold.candidatePackage -cne '8.0.1-preview.86'){throw 'Unexpected qualification request'}
$taskObservation=Get-Content $taskObservationPath -Raw|ConvertFrom-Json -DateKind String
if($taskObservation.state -cne 'WAITING_FOR_ROOT_GRANTED_SDK_SLOT_AND_EXPLICIT_RETURN' -or $taskObservation.nativeStarted){throw 'Original provider pre-launch hold not observed'}
$taskStart=Get-Content $taskStartPath -Raw|ConvertFrom-Json -DateKind String
$taskCoordinator=Get-Process -Id $taskStart.pid -ErrorAction Stop
if($taskCoordinator.Id -ne $taskObservation.coordinatorPid -or $taskCoordinator.StartTime.ToUniversalTime().Ticks -ne [DateTimeOffset]::Parse($taskStart.processStarted).UtcDateTime.Ticks){throw 'Provider coordination handle changed'}
$taskFullPath=Join-Path $taskEvidence 'full-verification.json'
$taskFull=Get-Content $taskFullPath -Raw|ConvertFrom-Json -DateKind String
$taskDeferred=Get-Content (Join-Path $taskEvidence 'step24-user-deferral.json') -Raw|ConvertFrom-Json -DateKind String
if($taskFull.configurations.Count -ne 2){throw 'Both complete regression configurations required'}
foreach($taskConfig in $taskFull.configurations){
 if($taskConfig.total -ne 6830 -or $taskConfig.failed -ne 24 -or $taskConfig.skipped -ne 0 -or $taskConfig.newFailureNames.Count -ne 0 -or !$taskConfig.actualNative62Matched){throw 'Unexpected full regression result'}
 if(@(Compare-Object @($taskDeferred.exactTestNames) @($taskConfig.failures|ForEach-Object name)).Count){throw 'Unapproved failure set'}
}
$taskFarPath=Join-Path $taskEvidence $(if($Revision -eq 5){'far90-runtime-verification.json'}else{'far90-runtime-'+$Revision+'-verification.json'})
$taskFar=Get-Content $taskFarPath -Raw|ConvertFrom-Json -DateKind String
if($taskFar.configurations.Count -ne 2 -or @($taskFar.configurations|Where-Object {$_.total -ne 90 -or !$_.actualNative62Matched -or !$_.originalSourceAndStopBrepBytesPreserved}).Count){throw 'Far90 lacks complete prerequisite terminal evidence'}
# A failed Far prerequisite remains failed; this grant cannot authorize its
# adoption. The SDK qualification slot is independent of the product result.
. (Join-Path $taskRoot 'artifacts/assert-cad-native-consumers-idle-2.ps1')
$taskIdle=@(Assert-CadNativeConsumersIdle)
[ordered]@{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');state='ROOT_GRANTED_SDK_CX_CY_NATIVE_SLOT_PENDING_MESSAGE_DISPATCH';sdkThread=$taskHold.sdkThread;candidatePackage=$taskHold.candidatePackage;requestedMinutes=30;actualCadConsumersIdle=$true;observedProviderCoordinator=$taskCoordinator.Id;coordinatorStarted=$taskStart.processStarted;holdSha256=(Get-FileHash $taskHoldPath).Hash;observationSha256=(Get-FileHash $taskObservationPath).Hash;fullAuditSha256=(Get-FileHash $taskFullPath).Hash;farAuditSha256=(Get-FileHash $taskFarPath).Hash;farPrerequisitePassed=$taskFar.prerequisitePassed;unrelatedProcessesPreserved=$taskIdle;frozenSDK86Changed=$false;desktopInputStarted=$false;AFComplete=$false}|ConvertTo-Json -Depth 6|Set-Content $taskGrantPath
Write-Output 'Actual safe boundary recorded. Send the explicit grant to the existing SDK task; do not start a Cadoryx consumer until explicit return and a fresh idle check.'
