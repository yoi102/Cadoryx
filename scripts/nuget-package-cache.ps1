# Resolve the archive that actually supplied a locked restore. This works with
# the global cache or an isolated NUGET_PACKAGES directory; no SDK checkout is needed.
function Get-CadoryxLockedPackageArchive {
    param(
        [Parameter(Mandatory)][string]$AssetsPath,
        [Parameter(Mandatory)][string]$LockPath,
        [Parameter(Mandatory)][string]$PackageId,
        [Parameter(Mandatory)][string]$Version
    )
    if ($PackageId -notmatch '^[A-Za-z0-9_.-]+$' -or $Version -notmatch '^[A-Za-z0-9.+-]+$') {
        throw 'Invalid locked package identity.'
    }
    $cadAssets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json
    $cadLock = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json
    $cadLocked = @($cadLock.dependencies.PSObject.Properties | ForEach-Object {
        $_.Value.PSObject.Properties | Where-Object Name -IEQ $PackageId
    })
    if (!$cadLocked.Count -or @($cadLocked | Where-Object { $_.Value.resolved -cne $Version -or !$_.Value.contentHash }).Count) {
        throw 'Package version or content hash differs from the requested lock.'
    }
    $cadHashes = @($cadLocked | ForEach-Object { $_.Value.contentHash } | Sort-Object -Unique -CaseSensitive)
    if ($cadHashes.Count -ne 1) { throw 'Locked framework package hashes differ.' }
    $cadKey = $PackageId + '/' + $Version
    $cadLibraries = @($cadAssets.libraries.PSObject.Properties | Where-Object Name -IEQ $cadKey)
    if ($cadLibraries.Count -ne 1 -or $cadLibraries[0].Value.type -cne 'package') {
        throw 'Requested package is not the exact restored package library.'
    }
    $cadLibrary = $cadLibraries[0].Value
    $cadAssetHash = [string]$cadLibrary.sha512
    if ($cadAssetHash.StartsWith('sha512-',[StringComparison]::Ordinal)) { $cadAssetHash=$cadAssetHash.Substring(7) }
    if ($cadAssetHash -cne $cadHashes[0]) { throw 'Restored package content hash differs from its lock.' }
    $cadExpectedRelative = $PackageId.ToLowerInvariant() + '/' + $Version.ToLowerInvariant()
    if ([string]$cadLibrary.path -cne $cadExpectedRelative) { throw 'Restored package cache path differs from its identity.' }
    $cadArchiveName = $PackageId.ToLowerInvariant() + '.' + $Version.ToLowerInvariant() + '.nupkg'
    $cadFound = @()
    foreach ($cadFolder in $cadAssets.packageFolders.PSObject.Properties) {
        $cadCacheRoot = [IO.Path]::GetFullPath($cadFolder.Name).TrimEnd([char[]]@('\','/')) + [IO.Path]::DirectorySeparatorChar
        $cadArchivePath = [IO.Path]::GetFullPath((Join-Path $cadCacheRoot ($cadExpectedRelative + '/' + $cadArchiveName)))
        if (!$cadArchivePath.StartsWith($cadCacheRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Restored archive escapes its cache root.' }
        if (!(Test-Path -LiteralPath $cadArchivePath -PathType Leaf)) { continue }
        $cadStream = [IO.File]::OpenRead($cadArchivePath)
        try { $cadActualHash = [Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData($cadStream)) }
        finally { $cadStream.Dispose() }
        $cadContentHash=$cadActualHash
        $cadRepositorySignatureVerified=$false
        if ($cadActualHash -cne $cadHashes[0]) {
            # NuGet lock files use the package content hash. Repository signing
            # changes the ZIP bytes while preserving this authenticated hash.
            $cadZip=[IO.Compression.ZipFile]::OpenRead($cadArchivePath)
            try{$cadSignatures=@($cadZip.Entries|Where-Object FullName -CEQ '.signature.p7s')}
            finally{$cadZip.Dispose()}
            if($cadSignatures.Count -ne 1){throw 'Cached nupkg bytes differ from the locked content hash.'}
            $cadPreviousLanguage=$env:DOTNET_CLI_UI_LANGUAGE
            try{
                $env:DOTNET_CLI_UI_LANGUAGE='en'
                $cadVerification=@(& dotnet nuget verify $cadArchivePath --all --verbosity normal 2>&1)
                $cadVerificationExit=$LASTEXITCODE
            }finally{$env:DOTNET_CLI_UI_LANGUAGE=$cadPreviousLanguage}
            if($cadVerificationExit -ne 0){throw 'Cached nupkg signature verification failed.'}
            $cadContentHashes=@([regex]::Matches(($cadVerification|Out-String),'(?m)^Content hash:\s*([A-Za-z0-9+/]+=*)\s*$')|ForEach-Object {$_.Groups[1].Value}|Sort-Object -Unique -CaseSensitive)
            if($cadContentHashes.Count -ne 1 -or $cadContentHashes[0] -cne $cadHashes[0]){throw 'Cached nupkg signed content differs from the locked content hash.'}
            $cadContentHash=$cadContentHashes[0]
            $cadRepositorySignatureVerified=$true
        }
        $cadFound += @([pscustomobject]@{packageId=$PackageId;version=$Version;archivePath=$cadArchivePath;contentHash=$cadContentHash;archiveSha512=$cadActualHash;signatureVerified=$cadRepositorySignatureVerified})
    }
    if (!$cadFound.Count) { throw 'Locked nupkg archive missing from restored caches; perform a fresh locked restore.' }
    # Identical locked bytes in several caches are equivalent, not a fallback
    # to a differently built package from a sibling repository.
    return $cadFound[0]
}

# The publisher freezes this after testing its own runtime. Consumers compare
# the entire native closure, including dependencies, rather than only the bridge.
function Get-CadoryxPublisherNativeManifest {
    param(
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$ExpectedBridgeSha256
    )
    if($ExpectedBridgeSha256 -notmatch '^[0-9A-Fa-f]{64}$'){throw 'Publisher-tested bridge SHA256 required'}
    $cadManifest=Get-Content -LiteralPath $ManifestPath -Raw|ConvertFrom-Json
    if($cadManifest.packageVersion -cne $Version -or $cadManifest.platform -cne 'windows' -or $cadManifest.architecture -cne 'x64' -or !$cadManifest.nativeAbi -or !$cadManifest.bridgeVersion){throw 'Exact publisher-tested Windows x64 runtime manifest required'}
    $cadNative=@{}
    foreach($cadFile in @($cadManifest.files|Where-Object path -CLike 'occt/*.dll')){
        $cadNameMatch=[regex]::Match([string]$cadFile.path,'^occt/([A-Za-z0-9_.-]+\.dll)$')
        if(!$cadNameMatch.Success -or $cadFile.sha256 -cnotmatch '^[0-9A-F]{64}$' -or [long]$cadFile.size -le 0){throw 'Publisher native manifest has an invalid file path, size or SHA256'}
        $cadName=$cadNameMatch.Groups[1].Value
        if($cadNative.ContainsKey($cadName)){throw 'Publisher native manifest has duplicate DLL names'}
        $cadNative[$cadName]=$cadFile.sha256
    }
    if(!$cadNative.Count -or !$cadNative.ContainsKey('OcctSharp.Native.dll') -or $cadNative['OcctSharp.Native.dll'] -cne $ExpectedBridgeSha256.ToUpperInvariant()){throw 'Publisher manifest bridge differs from its independently tested SHA256'}
    return [pscustomobject]@{manifest=$cadManifest;native=$cadNative;path=[IO.Path]::GetFullPath($ManifestPath);sha256=(Get-FileHash -LiteralPath $ManifestPath).Hash}
}
