param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$cadOutput=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $cadOutput){throw 'Fresh immutable fixture output required'}
$cadFixtureRoot=Join-Path $cadOutput 'synthetic-repository'
New-Item -ItemType Directory -Path (Join-Path $cadFixtureRoot 'scripts'),(Join-Path $cadFixtureRoot 'Consumer'),(Join-Path $cadFixtureRoot 'Cadoryx.wpf') | Out-Null
& git init -q $cadFixtureRoot
if($LASTEXITCODE -ne 0){throw 'Cannot initialize isolated synthetic fixture repository'}
$cadAuditScript=Join-Path $cadFixtureRoot 'scripts/verify-public-occt-locks.ps1'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'verify-public-occt-locks.ps1') -Destination $cadAuditScript
$cadVersion='8.0.1-preview.85'
$cadSha='A'*64
$cadHash=[Convert]::ToBase64String([byte[]]::new(64))
$cadFamily=@('OcctSharp','OcctSharp.Runtime','OcctSharp.Foundation','OcctSharp.Geometry','OcctSharp.MeshData','OcctSharp.Modeling','OcctSharp.Mesh','OcctSharp.Documents','OcctSharp.Visualization','OcctSharp.DataExchange','OcctSharp.Xde','OcctSharp.IVtk','OcctSharp.Draw','OcctSharp.Native.win-x64')
$cadPublic=@{state='PASS_FRESH_PUBLIC_ONLY_14_PACKAGE_LOCKED_RESTORE_EXACT_NATIVE_DISK_PAYLOAD_RUNTIME_NOT_RUN';version=$cadVersion;source='https://api.nuget.org/v3/index.json';nativeAbi='1.118';bridgeVersion='0.133.0';packages=@($cadFamily|ForEach-Object {@{id=$_;version=$cadVersion;source='https://api.nuget.org/v3/index.json';contentHash=$cadHash}});native=@(@{name='OcctSharp.Native.dll';sha256=$cadSha})}
$cadPublicPath=Join-Path $cadOutput 'synthetic-public-metadata.json'
$cadPublic|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $cadPublicPath -Encoding utf8
$cadBaseline=@{occtSharpPackage=$cadVersion;nativeSha256=$cadSha;nativeAbi='1.118';bridgeVersion='0.133.0'}|ConvertTo-Json
$cadLock=@{version=1;dependencies=@{'net10.0'=@{'OcctSharp'=@{type='Direct';resolved=$cadVersion;contentHash=$cadHash}}}}|ConvertTo-Json -Depth 8
$cadOriginal=@{
 'Directory.Build.props'="<Project><PropertyGroup><OcctSharpVersion>$cadVersion</OcctSharpVersion></PropertyGroup></Project>"
 'NuGet.Config'='<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>'
 'Cadoryx.wpf/runtime-baseline.json'=$cadBaseline
 'Consumer/Consumer.csproj'='<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="OcctSharp" Version="[$(OcctSharpVersion)]"/></ItemGroup></Project>'
 'Consumer/packages.lock.json'=$cadLock
}
$cadCases=@('consistent-public-metadata','wrong-lock-hash','private-lock-version','private-direct-reference','missing-direct-lock-entry','local-package-source','stale-abi')
$cadRows=@()
foreach($cadCase in $cadCases){
 foreach($cadEntry in $cadOriginal.GetEnumerator()){[IO.File]::WriteAllText((Join-Path $cadFixtureRoot $cadEntry.Key),$cadEntry.Value,[Text.UTF8Encoding]::new($false))}
 switch($cadCase){
  'wrong-lock-hash' {$cadChanged=$cadLock|ConvertFrom-Json;$cadChanged.dependencies.'net10.0'.OcctSharp.contentHash='wrong';$cadChanged|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $cadFixtureRoot 'Consumer/packages.lock.json') -Encoding utf8}
  'private-lock-version' {$cadChanged=$cadLock.Replace($cadVersion,'8.0.1-preview.31.cadoryx.curve-inspection.26');Set-Content -LiteralPath (Join-Path $cadFixtureRoot 'Consumer/packages.lock.json') -Value $cadChanged -Encoding utf8}
  'private-direct-reference' {$cadChanged=$cadOriginal['Consumer/Consumer.csproj'].Replace('[$(OcctSharpVersion)]','[8.0.1-preview.31.cadoryx.curve-inspection.26]');Set-Content -LiteralPath (Join-Path $cadFixtureRoot 'Consumer/Consumer.csproj') -Value $cadChanged -Encoding utf8}
  'missing-direct-lock-entry' {Set-Content -LiteralPath (Join-Path $cadFixtureRoot 'Consumer/packages.lock.json') -Value '{"version":1,"dependencies":{"net10.0":{}}}' -Encoding utf8}
  'local-package-source' {$cadChanged=$cadOriginal['NuGet.Config'].Replace('https://api.nuget.org/v3/index.json','../sibling-sdk/packages');Set-Content -LiteralPath (Join-Path $cadFixtureRoot 'NuGet.Config') -Value $cadChanged -Encoding utf8}
  'stale-abi' {$cadChanged=$cadBaseline.Replace('1.118','1.85');Set-Content -LiteralPath (Join-Path $cadFixtureRoot 'Cadoryx.wpf/runtime-baseline.json') -Value $cadChanged -Encoding utf8}
 }
 $cadAccepted=$false;$cadError=$null
 try{$null=& $cadAuditScript -PublicVerificationPath $cadPublicPath -OutputDirectory (Join-Path $cadOutput ('result-'+$cadCase));$cadAccepted=$true}catch{$cadError=$_.Exception.Message}
 $cadExpected=$cadCase -ceq 'consistent-public-metadata'
 if($cadAccepted -ne $cadExpected){throw ('Unexpected consumer fixture result: '+$cadCase+' / '+$cadError)}
 $cadRows+=@{scenario=$cadCase;passed=$true;accepted=$cadAccepted;rejection=$cadError}
}
@{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');state='PASS_SYNTHETIC_PUBLIC_CONSUMER_METADATA_FIXTURES_NOT_PUBLIC_RESTORE_OR_RUNTIME';count=$cadRows.Count;fixtures=$cadRows;scriptSha256=(Get-FileHash -LiteralPath $cadAuditScript).Hash;publicMetadataIsSynthetic=$true;nativeCodeExecuted=$false;publicRestoreRun=$false}|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $cadOutput 'verification.json') -Encoding utf8
Write-Output ('PASS '+$cadRows.Count+' synthetic public consumer metadata fixtures; actual public consumption NOT RUN.')
