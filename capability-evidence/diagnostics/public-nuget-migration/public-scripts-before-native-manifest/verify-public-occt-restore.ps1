param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$ExpectedNativeSha256
)
$ErrorActionPreference='Stop'
if($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$'){throw 'An exact published NuGet version is required'}
if($ExpectedNativeSha256 -notmatch '^[0-9A-Fa-f]{64}$'){throw 'Publisher native bridge SHA256 is required'}
. (Join-Path $PSScriptRoot 'nuget-package-cache.ps1')
$cadOutput=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $cadOutput){throw 'Fresh immutable public restore output required'}
New-Item -ItemType Directory -Path $cadOutput | Out-Null
$cadConfig=Join-Path $cadOutput 'NuGet.Config'
$cadProject=Join-Path $cadOutput 'PublishedOcctRestore.csproj'
$cadCache=Join-Path $cadOutput 'packages'
@'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
  <packageSourceMapping><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>
'@ | Set-Content -LiteralPath $cadConfig -Encoding utf8
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><RuntimeIdentifier>win-x64</RuntimeIdentifier><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile></PropertyGroup>
  <ItemGroup><PackageReference Include="OcctSharp" Version="[$Version]"/><PackageReference Include="OcctSharp.Native.win-x64" Version="[$Version]"/></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $cadProject -Encoding utf8
$cadStages=@()
try{
 foreach($cadStage in @('restore','restore-locked')){
  $cadArgs=@('restore',$cadProject,'--configfile',$cadConfig,'--packages',$cadCache,'--no-cache','--nologo','-v','minimal')
  if($cadStage -eq 'restore-locked'){$cadArgs+=@('--locked-mode')}
  & dotnet @cadArgs > (Join-Path $cadOutput ($cadStage+'.log')) 2> (Join-Path $cadOutput ($cadStage+'-stderr.log'))
  $cadCode=$LASTEXITCODE
  @{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');stage=$cadStage;exitCode=$cadCode;version=$Version;source='https://api.nuget.org/v3/index.json';freshCache=$cadCache;nativeCodeExecuted=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $cadOutput ($cadStage+'-exit.json')) -Encoding utf8
  $cadStages+=@{stage=$cadStage;exitCode=$cadCode}
  if($cadCode -ne 0){throw 'Public-only NuGet restore failed; original output preserved'}
 }
 $cadAssetsPath=Join-Path $cadOutput 'obj/project.assets.json'
 $cadLockPath=Join-Path $cadOutput 'packages.lock.json'
 $cadAssets=Get-Content -LiteralPath $cadAssetsPath -Raw|ConvertFrom-Json
 $cadSources=@($cadAssets.project.restore.sources.PSObject.Properties.Name)
 if($cadSources.Count -ne 1 -or $cadSources[0] -cne 'https://api.nuget.org/v3/index.json'){throw 'Restore used an unexpected source'}
 $cadLibraries=@($cadAssets.libraries.PSObject.Properties|Where-Object {$_.Name -like 'OcctSharp/*' -or $_.Name -like 'OcctSharp.*/*'})
 $cadExpectedFamily=@('OcctSharp','OcctSharp.Runtime','OcctSharp.Foundation','OcctSharp.Geometry','OcctSharp.MeshData','OcctSharp.Modeling','OcctSharp.Mesh','OcctSharp.Documents','OcctSharp.Visualization','OcctSharp.DataExchange','OcctSharp.Xde','OcctSharp.IVtk','OcctSharp.Draw','OcctSharp.Native.win-x64')
 $cadActualFamily=@($cadLibraries|ForEach-Object {$_.Name.Split('/')[0]})
 if($cadLibraries.Count -ne $cadExpectedFamily.Count -or @(Compare-Object -ReferenceObject $cadExpectedFamily -DifferenceObject $cadActualFamily -CaseSensitive).Count){throw 'Published OcctSharp package family differs from the exact expected14 IDs'}
 $cadPackages=@()
 foreach($cadLibrary in $cadLibraries){
  $cadParts=$cadLibrary.Name.Split('/')
  if($cadParts.Length -ne 2 -or $cadParts[1] -cne $Version){throw 'Published restore contains mixed SDK versions'}
  $cadResolved=Get-CadoryxLockedPackageArchive -AssetsPath $cadAssetsPath -LockPath $cadLockPath -PackageId $cadParts[0] -Version $Version
  $cadMetadataPath=Join-Path (Split-Path -Parent $cadResolved.archivePath) '.nupkg.metadata'
  $cadMetadata=Get-Content -LiteralPath $cadMetadataPath -Raw|ConvertFrom-Json
  if($cadMetadata.source -cne 'https://api.nuget.org/v3/index.json'){throw 'Package cache records a non-public origin'}
  $cadPackages+=@{id=$cadParts[0];version=$Version;archive=$cadResolved.archivePath;contentHash=$cadResolved.contentHash;sha256=(Get-FileHash -LiteralPath $cadResolved.archivePath).Hash;source=$cadMetadata.source}
 }
 $cadNative=$cadPackages|Where-Object id -CEQ 'OcctSharp.Native.win-x64'
 if(@($cadNative).Count -ne 1){throw 'Exactly one published native package required'}
 $cadArchive=[IO.Compression.ZipFile]::OpenRead($cadNative.archive)
 $cadNativePayload=@()
 try{
  foreach($cadEntry in @($cadArchive.Entries|Where-Object FullName -Like 'buildTransitive/win-x64/occt/*.dll')){
   $cadStream=$cadEntry.Open()
   try{$cadHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($cadStream))}finally{$cadStream.Dispose()}
   $cadNativePayload+=@{name=$cadEntry.Name;sha256=$cadHash}
  }
 }finally{$cadArchive.Dispose()}
 if($cadNativePayload.Count -ne 62 -or @($cadNativePayload.name|Sort-Object -Unique).Count -ne 62){throw 'Published native62 payload differs'}
 $cadBridge=$cadNativePayload|Where-Object name -CEQ 'OcctSharp.Native.dll'
 if(@($cadBridge).Count -ne 1 -or $cadBridge.sha256 -cne $ExpectedNativeSha256.ToUpperInvariant()){throw 'Published bridge differs from publisher-tested native payload'}
 @{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');state='PASS_FRESH_PUBLIC_ONLY_14_PACKAGE_LOCKED_RESTORE_NATIVE62_DISK_PAYLOAD_RUNTIME_NOT_RUN';version=$Version;source='https://api.nuget.org/v3/index.json';configSha256=(Get-FileHash -LiteralPath $cadConfig).Hash;lockSha256=(Get-FileHash -LiteralPath $cadLockPath).Hash;stages=$cadStages;packages=$cadPackages;native=$cadNativePayload;runtimePassed=$false;productionApplied=$false;cleanup=$false;AF='ACTIVE';G='USER_DEFERRED'}|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $cadOutput 'verification.json') -Encoding utf8
 Write-Output ('PASS public-only '+$Version+' locked14/native62 package restore; actual runtime/application acceptance pending.')
}catch{
 @{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');error=$_.ToString();completedStages=$cadStages;version=$Version;source='https://api.nuget.org/v3/index.json';runtimePassed=$false;cleanup=$false;AF='ACTIVE'}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $cadOutput 'failure.json') -Encoding utf8
 throw
}
