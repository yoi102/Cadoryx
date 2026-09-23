param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$cadRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if(!$OutputDirectory){$OutputDirectory=Join-Path $cadRoot ('artifacts/occt-capability-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
$cadOutput=[IO.Path]::GetFullPath($OutputDirectory)
$cadPublish=Join-Path $cadOutput 'publish'
$cadEvidence=Join-Path $cadOutput 'evidence'
New-Item -ItemType Directory -Path $cadOutput -Force | Out-Null
dotnet restore $PSScriptRoot --configfile (Join-Path $cadRoot 'NuGet.Config') --locked-mode --nologo -v minimal
if($LASTEXITCODE -ne 0){throw 'Locked restore failed.'}
dotnet publish $PSScriptRoot -c Release --no-restore --self-contained false -o $cadPublish --nologo -v minimal
if($LASTEXITCODE -ne 0){throw 'Publish failed.'}
[xml]$cadProps=Get-Content -LiteralPath (Join-Path $cadRoot 'Directory.Build.props') -Raw
$cadVersion=[string]$cadProps.Project.PropertyGroup.OcctSharpVersion
$cadLock=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'packages.lock.json') -Raw | ConvertFrom-Json
$cadDependencies=@($cadLock.dependencies.PSObject.Properties | ForEach-Object {$_.Value.PSObject.Properties} | Where-Object Name -Like 'OcctSharp*')
if($cadDependencies.Count -eq 0 -or @($cadDependencies | Where-Object {$_.Value.resolved -ne $cadVersion}).Count){throw 'Mixed OcctSharp versions.'}

# Check actual nupkg payloads, not an earlier build tree or PATH-installed runtime.
$cadPackage=Join-Path $cadRoot "../OcctSharp/OcctSharp/artifacts/packages/OcctSharp.Native.win-x64.$cadVersion.nupkg"
$cadArchive=[IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($cadPackage))
$cadPayload=@()
try{
    foreach($cadEntry in $cadArchive.Entries | Where-Object FullName -Like 'buildTransitive/win-x64/occt/*.dll'){
        $cadPath=Join-Path $cadPublish ('occt/'+$cadEntry.Name)
        $cadStream=$cadEntry.Open()
        try{$cadExpected=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($cadStream))}finally{$cadStream.Dispose()}
        $cadActual=(Get-FileHash -LiteralPath $cadPath -Algorithm SHA256).Hash
        if($cadExpected -ne $cadActual){throw "Native payload mismatch: $($cadEntry.Name)"}
        $cadPayload+=@{name=$cadEntry.Name;sha256=$cadActual}
    }
}finally{$cadArchive.Dispose()}
if(!$cadPayload.Count -or (Get-ChildItem -LiteralPath (Join-Path $cadPublish 'occt') -Filter *.dll -File).Count -ne $cadPayload.Count){throw 'Unexpected native payload file count.'}
$cadStart=[Diagnostics.ProcessStartInfo]::new()
$cadStart.FileName=Join-Path $cadPublish 'OcctCapabilityProbe.exe'
$cadStart.WorkingDirectory=$cadPublish
$cadStart.UseShellExecute=$false
$cadStart.CreateNoWindow=$true
$cadStart.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
$cadStart.ArgumentList.Add($cadEvidence)
$cadStart.Environment['PATH']="$env:WINDIR\System32;$env:WINDIR;$env:ProgramFiles\dotnet"
@($cadStart.Environment.Keys) | Where-Object {$_ -match '^(CSF_|CASROOT|OCCT|OCCTSHARP)'} | ForEach-Object {$cadStart.Environment.Remove($_) | Out-Null}
$cadRun=[Diagnostics.Process]::Start($cadStart)
try{
    if(!$cadRun.WaitForExit(60000)){$cadRun.Kill();$null=$cadRun.WaitForExit(10000);throw 'Probe timed out.'}
    if($cadRun.ExitCode -ne 0){throw "Capability probe failed; see $cadEvidence/result.json"}
}finally{$cadRun.Dispose()}
$cadResult=Get-Content -LiteralPath (Join-Path $cadEvidence 'result.json') -Raw | ConvertFrom-Json
if(!$cadResult.passed -or $cadResult.runtime.ManagedVersion -ne $cadVersion){throw 'Runtime identity or result mismatch.'}
@{passed=$true;version=$cadVersion;scenarios=$cadResult.total;nativeDlls=$cadPayload.Count;nativePayload=$cadPayload;packageSha256=(Get-FileHash -LiteralPath $cadPackage).Hash;
    packagePath=[IO.Path]::GetFullPath($cadPackage);publish=$cadPublish;evidence=$cadEvidence;frameworkDependent=$true} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $cadOutput 'package-audit.json') -Encoding utf8
Write-Output "PASS: $($cadResult.total) scenarios; $($cadPayload.Count) native DLLs match NuGet; version $cadVersion."
Write-Output "Evidence: $cadOutput"
