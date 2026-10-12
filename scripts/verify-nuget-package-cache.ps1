param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'nuget-package-cache.ps1')
$cadOutput=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $cadOutput){throw 'Immutable helper fixture output already exists'}
New-Item -ItemType Directory -Path $cadOutput | Out-Null
$cadId='Cadoryx.MetadataFixture'
$cadVersion='1.0.0'
$cadCache=Join-Path $cadOutput 'cache'
$cadPackageRoot=Join-Path $cadCache 'cadoryx.metadatafixture/1.0.0'
New-Item -ItemType Directory -Path $cadPackageRoot | Out-Null
$cadPackage=Join-Path $cadPackageRoot 'cadoryx.metadatafixture.1.0.0.nupkg'
$cadZip=[IO.Compression.ZipFile]::Open($cadPackage,[IO.Compression.ZipArchiveMode]::Create)
try{
 $cadEntry=$cadZip.CreateEntry('README.txt')
 $cadWriter=[IO.StreamWriter]::new($cadEntry.Open())
 try{$cadWriter.Write('Synthetic package-cache metadata fixture; no SDK/runtime acceptance.')}finally{$cadWriter.Dispose()}
}finally{$cadZip.Dispose()}
$cadHash=[Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData([IO.File]::ReadAllBytes($cadPackage)))
$cadAssets=Join-Path $cadOutput 'project.assets.json'
$cadLock=Join-Path $cadOutput 'packages.lock.json'
function Write-CadCacheFixture([string]$ResolvedVersion,[string]$LockHash,[string]$AssetHash,[string]$CacheRelative){
 @{libraries=@{($cadId+'/'+$cadVersion)=@{type='package';path=$CacheRelative;sha512=$AssetHash}};packageFolders=@{$cadCache=@{}}}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $cadAssets -Encoding utf8
 @{version=1;dependencies=@{'net10.0'=@{$cadId=@{type='Direct';resolved=$ResolvedVersion;contentHash=$LockHash}}}}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $cadLock -Encoding utf8
}
$cadRows=@()
function Expect-CadCacheRejection([string]$Name,[string]$ExpectedMessage){
 $cadRejected=$false
 try{$null=Get-CadoryxLockedPackageArchive -AssetsPath $cadAssets -LockPath $cadLock -PackageId $cadId -Version $cadVersion}
 catch{if($_.Exception.Message -notlike ($ExpectedMessage+'*')){throw};$cadRejected=$true}
 if(!$cadRejected){throw ('Unsafe package cache fixture accepted: '+$Name)}
 $script:cadRows+=@{name=$Name;rejected=$true}
}
Write-CadCacheFixture $cadVersion $cadHash $cadHash 'cadoryx.metadatafixture/1.0.0'
$cadActual=Get-CadoryxLockedPackageArchive -AssetsPath $cadAssets -LockPath $cadLock -PackageId $cadId -Version $cadVersion
if($cadActual.archivePath -cne $cadPackage -or $cadActual.contentHash -cne $cadHash){throw 'Exact cache archive not resolved'}
$cadRows+=@{name='EXACT_LOCKED_ARCHIVE';passed=$true}
Write-CadCacheFixture '2.0.0' $cadHash $cadHash 'cadoryx.metadatafixture/1.0.0'
Expect-CadCacheRejection 'WRONG_LOCK_VERSION' 'Package version or content hash differs'
Write-CadCacheFixture $cadVersion $cadHash 'DifferentAssetHash' 'cadoryx.metadatafixture/1.0.0'
Expect-CadCacheRejection 'ASSETS_LOCK_HASH_MISMATCH' 'Restored package content hash differs'
Write-CadCacheFixture $cadVersion $cadHash $cadHash '../other-package'
Expect-CadCacheRejection 'FOREIGN_CACHE_PATH' 'Restored package cache path differs'
Write-CadCacheFixture $cadVersion 'DifferentLockedBytes' 'DifferentLockedBytes' 'cadoryx.metadatafixture/1.0.0'
Expect-CadCacheRejection 'NUPKG_BYTES_MISMATCH' 'Cached nupkg bytes differ'
Write-CadCacheFixture $cadVersion $cadHash $cadHash 'cadoryx.metadatafixture/1.0.0'
Move-Item -LiteralPath $cadPackage -Destination ($cadPackage+'.fixture-preserved')
Expect-CadCacheRejection 'MISSING_CACHED_ARCHIVE' 'Locked nupkg archive missing'
Move-Item -LiteralPath ($cadPackage+'.fixture-preserved') -Destination $cadPackage
@{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');state='PASS_LOCKED_PACKAGE_CACHE_RESOLUTION_SIX_SYNTHETIC_CASES';count=$cadRows.Count;cases=$cadRows;publicSdkAccepted=$false;nativeCodeExecuted=$false;cleanup=$false}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $cadOutput 'verification.json') -Encoding utf8
Write-Output ('PASS '+$cadRows.Count+' package-cache cases; no SDK/runtime/publication acceptance.')
