param([Parameter(Mandatory=$true)][string]$DocumentPath,[string]$OutputPath)
$ErrorActionPreference='Stop'
$cadRoot=Split-Path -Parent $PSScriptRoot
$cadDocument=[IO.Path]::GetFullPath($DocumentPath)
if(!$OutputPath){$OutputPath=Join-Path $cadRoot ('artifacts/sketch-probe-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.json')}
$cadProject=Join-Path $cadRoot 'tools/SketchProbe/SketchProbe.csproj'
dotnet restore $cadProject --locked-mode --nologo -v minimal
if($LASTEXITCODE -ne 0){throw 'Sketch consumer locked restore failed.'}
dotnet build $cadProject -c Release --no-restore --nologo -v minimal
if($LASTEXITCODE -ne 0){throw 'Sketch consumer build failed.'}
dotnet (Join-Path $cadRoot 'tools/SketchProbe/bin/Release/net10.0/SketchProbe.dll') $cadDocument ([IO.Path]::GetFullPath($OutputPath))
if($LASTEXITCODE -ne 0){throw 'Sketch consumer failed.'}
Write-Output "Sketch consumer evidence: $OutputPath"
