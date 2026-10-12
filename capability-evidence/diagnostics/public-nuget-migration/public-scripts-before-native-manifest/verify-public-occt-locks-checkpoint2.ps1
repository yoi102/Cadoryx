param(
    [Parameter(Mandatory)][string]$PublicVerificationPath,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$cadRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cadOutput=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $cadOutput){throw 'Fresh lock audit output required'}
New-Item -ItemType Directory -Path $cadOutput | Out-Null
try{
 $cadPublic=Get-Content -LiteralPath $PublicVerificationPath -Raw|ConvertFrom-Json
 if($cadPublic.state -cne 'PASS_FRESH_PUBLIC_ONLY_14_PACKAGE_LOCKED_RESTORE_NATIVE62_DISK_PAYLOAD_RUNTIME_NOT_RUN' -or $cadPublic.source -cne 'https://api.nuget.org/v3/index.json'){throw 'A completed fresh public restore verification is required'}
 $cadVersion=[string]$cadPublic.version
 [xml]$cadProps=Get-Content -LiteralPath (Join-Path $cadRoot 'Directory.Build.props') -Raw
 if([string]$cadProps.Project.PropertyGroup.OcctSharpVersion -cne $cadVersion){throw 'Production SDK version differs from verified public version'}
 [xml]$cadConfig=Get-Content -LiteralPath (Join-Path $cadRoot 'NuGet.Config') -Raw
 $cadSources=@($cadConfig.configuration.packageSources.add)
 if(!$cadConfig.configuration.packageSources.clear -and !$cadConfig.SelectSingleNode('/configuration/packageSources/clear')){throw 'Public configuration must clear inherited package sources'}
 if($cadSources.Count -ne 1 -or $cadSources[0].value -cne 'https://api.nuget.org/v3/index.json'){throw 'Production configuration must use only nuget.org'}
 if(@($cadConfig.SelectNodes('/configuration/fallbackPackageFolders/add')).Count){throw 'Production configuration includes a local fallback folder'}
 $cadBaseline=Get-Content -LiteralPath (Join-Path $cadRoot 'Cadoryx.wpf/runtime-baseline.json') -Raw|ConvertFrom-Json
 $cadBridge=@($cadPublic.native|Where-Object name -CEQ 'OcctSharp.Native.dll')
 if($cadBridge.Count -ne 1 -or $cadBaseline.occtSharpPackage -cne $cadVersion -or $cadBaseline.nativeSha256.ToUpperInvariant() -cne $cadBridge[0].sha256){throw 'Runtime baseline package or bridge differs from the verified public payload'}
 $cadPackageMap=@{}
 foreach($cadPackage in $cadPublic.packages){
  if($cadPackageMap.ContainsKey($cadPackage.id) -or $cadPackage.version -cne $cadVersion -or $cadPackage.source -cne $cadPublic.source){throw 'Public package verification has duplicate IDs or inconsistent versions/origins'}
  $cadPackageMap[$cadPackage.id]=$cadPackage.contentHash
 }
 if($cadPackageMap.Count -ne 14){throw 'Complete verified public package family required'}
 $cadFiles=@(& git -C $cadRoot ls-files --cached --others --exclude-standard -- '**/packages.lock.json' '*.csproj')
 if($LASTEXITCODE -ne 0){throw 'Cannot enumerate repository consumer files'}
 $cadLocks=@();$cadReferences=@();$cadLockReferences=0
 foreach($cadRelative in @($cadFiles|Sort-Object -Unique)){
  # Frozen diagnostic projects describe historical payloads, not active consumers.
  if($cadRelative.StartsWith('capability-evidence/',[StringComparison]::Ordinal)){continue}
  $cadPath=Join-Path $cadRoot $cadRelative
  if($cadRelative.EndsWith('/packages.lock.json',[StringComparison]::Ordinal)){
   $cadLock=Get-Content -LiteralPath $cadPath -Raw|ConvertFrom-Json
   $cadCount=0
   foreach($cadFramework in $cadLock.dependencies.PSObject.Properties){
    foreach($cadDependency in $cadFramework.Value.PSObject.Properties){
     if($cadDependency.Name -cne 'OcctSharp' -and !$cadDependency.Name.StartsWith('OcctSharp.',[StringComparison]::Ordinal)){continue}
     if(!$cadPackageMap.ContainsKey($cadDependency.Name) -or $cadDependency.Value.resolved -cne $cadVersion -or $cadDependency.Value.contentHash -cne $cadPackageMap[$cadDependency.Name]){throw "Consumer lock differs from public package bytes: $cadRelative / $($cadDependency.Name)"}
     $cadCount++
    }
   }
   if($cadCount){$cadLocks+=@{path=$cadRelative;sha256=(Get-FileHash -LiteralPath $cadPath).Hash;references=$cadCount};$cadLockReferences+=$cadCount}
  }elseif($cadRelative.EndsWith('.csproj',[StringComparison]::Ordinal)){
   [xml]$cadProject=Get-Content -LiteralPath $cadPath -Raw
   foreach($cadReference in $cadProject.SelectNodes('//PackageReference')){
    $cadId=$cadReference.GetAttribute('Include')
    if($cadId -cne 'OcctSharp' -and !$cadId.StartsWith('OcctSharp.',[StringComparison]::Ordinal)){continue}
    if(!$cadPackageMap.ContainsKey($cadId) -or $cadReference.GetAttribute('Version') -cne '[$(OcctSharpVersion)]'){throw "Consumer must use the shared exact SDK version: $cadRelative / $cadId"}
    if($cadProject.SelectSingleNode('//OcctSharpVersion')){throw "Consumer overrides the shared SDK version: $cadRelative"}
    if(!(Test-Path -LiteralPath (Join-Path (Split-Path -Parent $cadPath) 'packages.lock.json'))){throw "Consumer lock missing: $cadRelative"}
    $cadReferences+=@{project=$cadRelative;package=$cadId;versionExpression=$cadReference.GetAttribute('Version')}
   }
  }
 }
 if(!$cadLocks.Count -or !$cadReferences.Count){throw 'No active SDK consumers audited'}
 @{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');state='PASS_PUBLIC_SOURCE_SHARED_VERSION_ALL_ACTIVE_LOCKS_AND_DISK_BASELINE_RUNTIME_NOT_RUN';version=$cadVersion;publicVerification=[IO.Path]::GetFullPath($PublicVerificationPath);publicVerificationSha256=(Get-FileHash -LiteralPath $PublicVerificationPath).Hash;lockReferences=$cadLockReferences;locks=$cadLocks;directReferences=$cadReferences;runtimePassed=$false;AF='ACTIVE';G='USER_DEFERRED'}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $cadOutput 'verification.json') -Encoding utf8
 Write-Output "PASS: $($cadLocks.Count) SDK consumer locks / $cadLockReferences references match verified public $cadVersion; runtime acceptance pending."
}catch{
 @{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');error=$_.ToString();runtimePassed=$false;AF='ACTIVE'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $cadOutput 'failure.json') -Encoding utf8
 throw
}
