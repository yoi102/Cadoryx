[CmdletBinding()]
param([string]$Repository='C:\Users\yoiri\source\repos\OcctSharp',
      [string]$Version='8.0.1-preview.28.cadoryx.h2b2.2')
$ErrorActionPreference='Stop'
$cadSource=Join-Path (Resolve-Path -LiteralPath $Repository).Path 'OcctSharp'
$cadNative=Join-Path $cadSource 'artifacts/native/Release'
$cadPackages=Join-Path $cadSource 'artifacts/packages'
if(!(Test-Path -LiteralPath (Join-Path $cadNative 'OcctSharp.Native.dll'))){throw 'Build the native Release bridge first.'}
foreach($cadModule in @('Native.win-x64','Runtime','Foundation','Geometry','MeshData','Modeling','Mesh','Documents','Visualization','DataExchange','Xde','IVtk','Draw','Facade')){
    $cadName=if($cadModule -eq 'Facade'){'OcctSharp'}else{'OcctSharp.'+$cadModule}
    $cadProject=Join-Path $cadSource "src/$cadName/$cadName.csproj"
    # An explicit runtime directory selects the new DLL; no committed baseline runtime is overwritten.
    & dotnet pack $cadProject -c Release --no-restore --nologo -v minimal -o $cadPackages "-p:PackageVersion=$Version" "-p:OcctSharpPackageVersion=$Version" "-p:OcctSharpNativeRuntimeDir=$cadNative" -p:OcctSharpUseBundledNativeRuntime=true
    if($LASTEXITCODE -ne 0){throw "Package build failed: $cadName"}
}
Write-Output "Created 14 local development packages: $Version. Nothing published."
