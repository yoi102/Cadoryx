[CmdletBinding()]
param([switch]$UseUiCulturePatch)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx'
$taskUseUiCulturePatch=$UseUiCulturePatch.IsPresent
. (Join-Path $taskRoot 'tools/CurrentCadCandidate/assert-performance-ready.ps1')
. (Join-Path $taskRoot 'artifacts/assert-cad-native-consumers-idle-2.ps1')
$null=Assert-CadNativeConsumersIdle
if(Get-Process testhost,Cadoryx.wpf,CadoryxStyleDefaults -ErrorAction SilentlyContinue){throw 'Native consumers remain serialized; preserve existing processes'}
foreach($taskRow in $taskFreeze.payload){if((Get-FileHash -LiteralPath (Join-Path $taskFreeze.candidate $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Frozen candidate changed'}}
$taskImageProof=Get-Content -LiteralPath (Join-Path $taskRoot 'artifacts/a5-production-image-verification-1.json') -Raw|ConvertFrom-Json
$taskExpectedImage='2E8022953A2D1D2118E6FBC4DDC8A964D7EEC1ACA8CA6B2AF055131595E63852'
if($taskImageProof.currentExpectedImageSha256.ToUpperInvariant() -cne $taskExpectedImage){throw 'Accepted full PNG reference changed'}
if((Get-FileHash -LiteralPath $taskImageProof.currentAccepted11Reference.path).Hash -cne $taskExpectedImage){throw 'Accepted reference bytes changed'}
$taskVerifierPath=$taskImageProof.instrumentedVerifier.path
if((Get-FileHash -LiteralPath $taskVerifierPath).Hash -cne $taskImageProof.instrumentedVerifier.sha256){throw 'Original strict verifier changed'}
$taskNativeRows=@($taskFreeze.native);if($taskNativeRows.Count -ne 62){throw 'Native62 required'}
$taskNativeDirectory=Join-Path $taskFreeze.candidate 'occt';$taskSources=1185
$taskTemplate=[IO.File]::ReadAllText($taskVerifierPath)
Write-Output 'Current F5+D7/local87cu1 two fresh production opens; original900s/counts/full788x411-PNG; timings separate from direct6482ms target.'
& ([ScriptBlock]::Create($taskTemplate)) -Executable (Join-Path $taskFreeze.candidate 'Cadoryx.wpf.exe') `
 -InputFile 'C:/Users/yoiri/Downloads/MiniPC3.step' -Output (Join-Path $taskRoot 'artifacts/af1082-f5-d7-production-open-1') `
 -Runs 2 -TimeoutSeconds 900 -ExpectedScreenshotSha256 $taskExpectedImage
