[CmdletBinding()]
param([string]$Repository='C:\Users\yoiri\source\repos\OcctSharp')
$ErrorActionPreference='Stop'
$cadSource=Join-Path $Repository 'OcctSharp'
$cadDump='C:\Program Files\Microsoft Visual Studio\18\Enterprise\VC\Tools\MSVC\14.51.36231\bin\Hostx64\x64\dumpbin.exe'
function Get-CadExports([string]$Path){
    $cadOutput=& $cadDump /exports $Path
    if($LASTEXITCODE -ne 0){throw "Cannot inspect $Path"}
    @($cadOutput | ForEach-Object {if($_ -match '^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+(\S+)'){$Matches[1]}} | Sort-Object -Unique)
}
$cadBaseline=Get-CadExports (Join-Path $cadSource 'runtime/win-x64/occt/OcctSharp.Native.dll')
$cadRelease=Get-CadExports (Join-Path $cadSource 'artifacts/native/Release/OcctSharp.Native.dll')
$cadDebug=Get-CadExports (Join-Path $cadSource 'artifacts/native/Debug/OcctSharp.Native.dll')
$cadBaselineSet=[Collections.Generic.HashSet[string]]::new([string[]]$cadBaseline,[StringComparer]::Ordinal)
$cadReleaseSet=[Collections.Generic.HashSet[string]]::new([string[]]$cadRelease,[StringComparer]::Ordinal)
$cadRemoved=@($cadBaseline | Where-Object {!$cadReleaseSet.Contains($_)})
$cadAdded=@($cadRelease | Where-Object {!$cadBaselineSet.Contains($_)})
if($cadRemoved.Count -or $cadAdded.Count -ne 1 -or $cadAdded[0] -ne 'occtsharp_boolean_topology_history'){throw 'Unexpected Release export delta.'}
if(!$cadReleaseSet.SetEquals([string[]]$cadDebug)){throw 'Release/Debug export sets differ.'}
$cadHashes=@{}
foreach($cadConfig in @('Release','Debug')){
    $cadRuntime=Join-Path $cadSource "artifacts/native/$cadConfig"
    $cadTest=Join-Path $cadSource "tests/OcctSharp.Runtime.Tests/bin/$cadConfig/net10.0/win-x64/occt"
    $cadDlls=@(Get-ChildItem -LiteralPath $cadRuntime -Filter '*.dll' -File)
    foreach($cadDll in $cadDlls){
        $cadExpected=(Get-FileHash -LiteralPath $cadDll.FullName).Hash
        if((Get-FileHash -LiteralPath (Join-Path $cadTest $cadDll.Name)).Hash -ne $cadExpected){throw "Wrong $cadConfig runtime copied into tests: $($cadDll.Name)"}
    }
    $cadHashes[$cadConfig]=@{NativeSha256=(Get-FileHash -LiteralPath (Join-Path $cadRuntime 'OcctSharp.Native.dll')).Hash;MatchedDlls=$cadDlls.Count}
}
@{BaselineExports=$cadBaseline.Count;ReleaseExports=$cadRelease.Count;Added=$cadAdded;Removed=$cadRemoved;DebugParity=$true;Runtime=$cadHashes} | ConvertTo-Json -Depth 5
