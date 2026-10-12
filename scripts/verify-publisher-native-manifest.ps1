param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'nuget-package-cache.ps1')
$cadOutput=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $cadOutput){throw 'Fresh immutable fixture output required'}
New-Item -ItemType Directory -Path $cadOutput | Out-Null
$cadSource=Get-Content -LiteralPath $ManifestPath -Raw|ConvertFrom-Json
$cadVersion=$cadSource.packageVersion
$cadBridge=($cadSource.files|Where-Object path -CEQ 'occt/OcctSharp.Native.dll').sha256
$cadOriginal=Get-CadoryxPublisherNativeManifest -ManifestPath $ManifestPath -Version $cadVersion -ExpectedBridgeSha256 $cadBridge
$cadRows=@(@{scenario='actual-publisher-manifest';passed=$true;nativeCount=$cadOriginal.native.Count})
$cadCases=@('wrong-version','missing-abi','duplicate-dll','traversal-path','invalid-sha256','zero-size','different-bridge')
foreach($cadCase in $cadCases){
 $cadMutation=Get-Content -LiteralPath $ManifestPath -Raw|ConvertFrom-Json
 $cadDll=@($cadMutation.files|Where-Object path -CLike 'occt/*.dll')[0]
 switch($cadCase){
  'wrong-version' {$cadMutation.packageVersion='0.0.0'}
  'missing-abi' {$cadMutation.nativeAbi=''}
  'duplicate-dll' {$cadMutation.files+=@($cadDll)}
  'traversal-path' {$cadDll.path='occt/../escape.dll'}
  'invalid-sha256' {$cadDll.sha256='bad'}
  'zero-size' {$cadDll.size=0}
  'different-bridge' {($cadMutation.files|Where-Object path -CEQ 'occt/OcctSharp.Native.dll').sha256='0'*64}
 }
 $cadPath=Join-Path $cadOutput ($cadCase+'.json')
 $cadMutation|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $cadPath -Encoding utf8
 $cadRejected=$false
 try{$null=Get-CadoryxPublisherNativeManifest -ManifestPath $cadPath -Version $cadVersion -ExpectedBridgeSha256 $cadBridge}catch{$cadRejected=$true}
 if(!$cadRejected){throw ('Invalid publisher fixture accepted: '+$cadCase)}
 $cadRows+=@{scenario=$cadCase;passed=$true;rejected=$true}
}
@{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');state='PASS_NATIVE_MANIFEST_METADATA_FIXTURES_NOT_PUBLIC_RESTORE_OR_RUNTIME';source=$cadOriginal.path;sourceSha256=$cadOriginal.sha256;count=$cadRows.Count;fixtures=$cadRows;nativeCodeExecuted=$false;publicRestoreRun=$false}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $cadOutput 'verification.json') -Encoding utf8
Write-Output ('PASS '+$cadRows.Count+' publisher manifest metadata fixtures; public restore and runtime NOT RUN.')
