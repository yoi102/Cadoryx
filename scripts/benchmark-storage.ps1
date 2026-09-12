param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$cadRoot=Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory=Join-Path $cadRoot ('artifacts\storage-bench-'+(Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
dotnet restore (Join-Path $cadRoot 'tools/StorageBench/StorageBench.csproj') --locked-mode --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Benchmark locked restore failed.' }
dotnet build (Join-Path $cadRoot 'tools/StorageBench/StorageBench.csproj') -c Release --no-restore --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Benchmark build failed.' }
$cadBenchmark=Join-Path $cadRoot 'tools/StorageBench/bin/Release/net10.0/StorageBench.dll'
foreach ($cadCase in @('shared-25000','brep-8000','opaque-128m')) {
    dotnet $cadBenchmark --case $cadCase $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw "Storage benchmark failed: $cadCase" }
}
# A separate process proves that metadata-only reads do not initialize the native DLL.
$cadMetadata = dotnet $cadBenchmark --settings-only (Join-Path $OutputDirectory 'brep-8000.cadoryx')
if ($LASTEXITCODE -ne 0) { throw 'Metadata-only consumer failed.' }
$cadMetadata | Set-Content -LiteralPath (Join-Path $OutputDirectory 'settings-only.json') -Encoding utf8
Write-Output $cadMetadata
Write-Output "Storage benchmark evidence: $OutputDirectory"
