param(
 [Parameter(Mandatory)][string]$PublicVerificationPath,
 [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'nuget-package-cache.ps1')
$cadPublic=Get-Content -LiteralPath $PublicVerificationPath -Raw|ConvertFrom-Json
$cadNative=@($cadPublic.packages|Where-Object id -CEQ 'OcctSharp.Native.win-x64')
if($cadNative.Count -ne 1 -or !$cadNative[0].signatureVerified){throw 'An actual verified public signed native package required'}
$cadOutput=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path $cadOutput){throw 'Fresh immutable signature fixture output required'}
$cadCache=Join-Path $cadOutput 'packages'
$cadFolder=Join-Path $cadCache ('occtsharp.native.win-x64/'+$cadPublic.version)
New-Item -ItemType Directory -Path $cadFolder|Out-Null
$cadArchive=Join-Path $cadFolder ('occtsharp.native.win-x64.'+$cadPublic.version+'.nupkg')
Copy-Item -LiteralPath $cadNative[0].archive -Destination $cadArchive
$cadAssets=Join-Path $cadOutput 'project.assets.json'
$cadLock=Join-Path $cadOutput 'packages.lock.json'
function Write-CadSignedMetadata([string]$Hash){
 @{libraries=@{('OcctSharp.Native.win-x64/'+$cadPublic.version)=@{type='package';path=('occtsharp.native.win-x64/'+$cadPublic.version);sha512=$Hash}};packageFolders=@{$cadCache=@{}}}|ConvertTo-Json -Depth 6|Set-Content $cadAssets
 @{version=1;dependencies=@{'net10.0'=@{'OcctSharp.Native.win-x64'=@{type='Direct';resolved=$cadPublic.version;contentHash=$Hash}}}}|ConvertTo-Json -Depth 6|Set-Content $cadLock
}
function Resolve-CadSignedFixture{
 Get-CadoryxLockedPackageArchive -AssetsPath $cadAssets -LockPath $cadLock -PackageId OcctSharp.Native.win-x64 -Version $cadPublic.version
}
$cadRows=@()
Write-CadSignedMetadata $cadNative[0].contentHash
$cadResolved=Resolve-CadSignedFixture
if(!$cadResolved.signatureVerified -or $cadResolved.contentHash -cne $cadNative[0].contentHash -or $cadResolved.archiveSha512 -cne $cadNative[0].archiveSha512){throw 'Actual signed archive identity mismatch'}
$cadRows+=@{scenario='valid-repository-signed-archive';passed=$true}
Write-CadSignedMetadata ([Convert]::ToBase64String([byte[]]::new(64)))
$cadRejected=$false
try{$null=Resolve-CadSignedFixture}catch{
 if($_.Exception.Message -cne 'Cached nupkg signed content differs from the locked content hash.'){throw}
 $cadRejected=$true
}
if(!$cadRejected){throw 'Wrong signed content lock accepted'}
$cadRows+=@{scenario='wrong-signed-content-lock';rejected=$true}
Write-CadSignedMetadata $cadNative[0].contentHash
$cadZip=[IO.Compression.ZipFile]::Open($cadArchive,[IO.Compression.ZipArchiveMode]::Update)
try{
 $cadEntry=$cadZip.CreateEntry('fixture-tampered-entry.txt')
 $cadWriter=[IO.StreamWriter]::new($cadEntry.Open())
 try{$cadWriter.Write('Intentional isolated signature corruption fixture.')}finally{$cadWriter.Dispose()}
}finally{$cadZip.Dispose()}
$cadRejected=$false
try{$null=Resolve-CadSignedFixture}catch{
 if($_.Exception.Message -cne 'Cached nupkg signature verification failed.'){throw}
 $cadRejected=$true
}
if(!$cadRejected){throw 'Tampered signed archive accepted'}
$cadRows+=@{scenario='tampered-signed-archive';rejected=$true}
@{state='PASS_ACTUAL_SIGNED_PACKAGE_ARCHIVE_CONTENT_HASH_AND_TAMPER_REJECTIONS_NOT_RUNTIME';count=$cadRows.Count;cases=$cadRows;originalPublicArchive=$cadNative[0].archive;originalPublicArchiveSha256=(Get-FileHash $cadNative[0].archive).Hash;scriptSha256=(Get-FileHash (Join-Path $PSScriptRoot 'nuget-package-cache.ps1')).Hash;nativeCodeExecuted=$false;cleanup=$false}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $cadOutput 'verification.json')
Write-Output 'PASS3 actual signed archive/content-lock/signature corruption scenarios; no native runtime executed.'
