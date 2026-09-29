param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$InputFile,
    [string]$Output,
    [int]$Runs=2,
    [int]$TimeoutSeconds=900,
    [string]$ExpectedSha256='f3773a7f37bd0cb8e717e4be6f1070a72e5c6e7b2f36f26f9501183df8c4c68e',
    [string]$ExpectedScreenshotSha256='679ce900e0dce29f0401a3eddd0e7befb33c6bbf669334712839f7cea16e4ee6'
)
$ErrorActionPreference='Stop'
if($Runs -lt 1 -or $Runs -gt 10){throw 'Runs must be 1..10.'}
$cadExe=(Resolve-Path -LiteralPath $Executable).Path
$cadInput=(Resolve-Path -LiteralPath $InputFile).Path
$actualHash=(Get-FileHash -LiteralPath $cadInput -Algorithm SHA256).Hash.ToLowerInvariant()
if($ExpectedSha256 -and $actualHash -ne $ExpectedSha256.ToLowerInvariant()){
    throw "Input SHA-256 mismatch: $actualHash"
}
if(!$Output){$Output=Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts/m14-open-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
$cadOutput=[IO.Path]::GetFullPath($Output)
if(Test-Path -LiteralPath $cadOutput){throw 'Choose a new output directory to preserve earlier evidence.'}
[IO.Directory]::CreateDirectory($cadOutput) | Out-Null
$reports=@()
for($i=1;$i -le $Runs;$i++){
    $runOutput=Join-Path $cadOutput ('run-'+$i)
    $start=[Diagnostics.ProcessStartInfo]::new($cadExe)
    $start.WorkingDirectory=Split-Path -Parent $cadExe
    $start.UseShellExecute=$false
    $start.CreateNoWindow=$true
    $start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $start.ArgumentList.Add('--m14-open-benchmark')
    $start.ArgumentList.Add($cadInput)
    $start.ArgumentList.Add($runOutput)
    $process=[Diagnostics.Process]::Start($start)
    try{
        $deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        while(!$process.WaitForExit(1000)){
            if([DateTime]::UtcNow -gt $deadline){$process.Kill($true);throw "M14 run $i timed out."}
        }
        $result=Join-Path $runOutput 'result.txt'
        if(!(Test-Path -LiteralPath $result)){throw "M14 run $i exited without a result (exit $($process.ExitCode))."}
        $resultText=Get-Content -LiteralPath $result -Raw
        if($process.ExitCode -ne 0 -or !$resultText.StartsWith('PASS:')){throw "M14 run $i failed: $resultText"}
        $report=Get-Content -LiteralPath (Join-Path $runOutput 'report.json') -Raw | ConvertFrom-Json
        if($report.sourceSha256 -ne $actualHash){throw "M14 run $i source hash changed."}
        if($null -eq $report.import -or $null -eq $report.scene){throw "M14 run $i has no production import or scene timing."}
        if($report.visibleInstances -ne 4975 -or $report.scene.VisibleInstances -ne 4975 -or
           $report.bodies -ne 1196 -or $report.nativeGeometry -ne 1196 -or $report.xdeContexts -ne 1){
            throw "M14 run $i lost instance, geometry, or XDE context data."
        }
        $screenshotHash=(Get-FileHash -LiteralPath (Join-Path $runOutput 'full-scene.png') -Algorithm SHA256).Hash.ToLowerInvariant()
        if($ExpectedScreenshotSha256 -and $screenshotHash -ne $ExpectedScreenshotSha256.ToLowerInvariant()){
            throw "M14 run $i full-scene screenshot differs: $screenshotHash"
        }
        $reports+=@{run=$i;openedMs=$report.openedMs;firstVisibleFromClickMs=$report.firstVisibleFromClickMs;
            fullSceneFromClickMs=$report.fullSceneFromClickMs;sceneCompleteMs=$report.scene.CompleteMs;
            visibleInstances=$report.visibleInstances;temporaryBytes=$report.import.TemporaryBytes;
            screenshotSha256=$screenshotHash;workerKernelImportMs=$report.import.Worker.KernelImportMs;
            nativeDisplayMs=$report.submissionProfile.DisplayMs;nativeRedrawMs=$report.submissionProfile.RedrawMs}
    }
    finally{$process.Dispose()}
}
$hardware=@{
    recordedUtc=[DateTime]::UtcNow.ToString('O');sourceSha256=$actualHash;sourceBytes=(Get-Item -LiteralPath $cadInput).Length
    cpu=@(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors)
    gpu=@(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion)
    physicalMemoryBytes=(Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
    cacheCondition='OS file cache, antivirus, and concurrent load were not controlled'
    runs=$reports
}
$hardware | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $cadOutput 'summary.json') -Encoding utf8
Write-Output "PASS: $Runs production STEP open runs; evidence: $cadOutput"
