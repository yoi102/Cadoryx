function Assert-CurrentSdkNativeReturn {
 param([string]$TaskRoot)
 $taskLatestGrant=Join-Path $TaskRoot 'artifacts/af1082-f5-d7-root-integration-1/sdk-native-return224-2.json'
 if(Test-Path $taskLatestGrant){
  $taskCurrentReturn=Join-Path $TaskRoot 'artifacts/af1082-f5-d7-root-integration-1/sdk-explicit-return224-received1.json'
  if(!(Test-Path $taskCurrentReturn)){throw 'Current224 SDK native lane remains reserved; the prior204 return cannot authorize another CAD runtime'}
  $taskCurrent=Get-Content $taskCurrentReturn -Raw|ConvertFrom-Json -DateKind String
  if(!$taskCurrent.sdkExplicitNativeReturnReceived -or $taskCurrent.acceptedFrozenPackageVersion -cne '8.0.1-preview.87.cu1' -or $taskCurrent.parentGrantSHA256 -cne (Get-FileHash $taskLatestGrant).Hash){throw 'Current224 return must explicitly cover the latest reservation and unchanged package'}
  if((Get-FileHash $taskCurrent.sdkConfirmation.path).Hash -cne $taskCurrent.sdkConfirmation.sha256){throw 'SDK current224 explicit return source changed'}
 }
 $taskReturn=Join-Path $TaskRoot 'artifacts/af1082-f5-d7-local87cu1-1/sdk-native-return.json'
 if(!(Test-Path $taskReturn)){throw 'Current SDK native reservation has not been explicitly returned'}
 $taskReceipt=Get-Content $taskReturn -Raw|ConvertFrom-Json -DateKind String
 if(!$taskReceipt.sdkExplicitNativeReturnReceived -or !$taskReceipt.rootMayStartCurrent7117 -or $taskReceipt.acceptedFrozenPackageVersion -cne '8.0.1-preview.87.cu1'){
  throw 'Current native reservation return is invalid for the frozen87.cu1 candidate'
 }
}
