function Assert-CurrentSdkNativeReturn {
 param([string]$TaskRoot)
 $taskReturn=Join-Path $TaskRoot 'artifacts/af1082-f5-d7-local87cu1-1/sdk-native-return.json'
 if(!(Test-Path $taskReturn)){throw 'Current SDK native reservation has not been explicitly returned'}
 $taskReceipt=Get-Content $taskReturn -Raw|ConvertFrom-Json -DateKind String
 if(!$taskReceipt.sdkExplicitNativeReturnReceived -or !$taskReceipt.rootMayStartCurrent7117 -or $taskReceipt.acceptedFrozenPackageVersion -cne '8.0.1-preview.87.cu1'){
  throw 'Current native reservation return is invalid for the frozen87.cu1 candidate'
 }
}
