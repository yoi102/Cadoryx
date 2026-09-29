param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string[]]$InputFiles,
    [int]$Cycles=20,
    [int]$TimeoutSeconds=1800,
    [string]$Output
)
$ErrorActionPreference='Stop'
$cadRoot=Split-Path -Parent $PSScriptRoot
$cadExe=(Resolve-Path -LiteralPath $Executable).Path
$cadInputs=@($InputFiles | ForEach-Object {(Resolve-Path -LiteralPath $_).Path})
if(!$Output){$Output=Join-Path $cadRoot ('artifacts/m6-benchmark-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
$cadOutput=[IO.Path]::GetFullPath($Output)
if(Test-Path -LiteralPath $cadOutput){throw 'Choose a new output directory to preserve earlier evidence.'}
$cadStart=[Diagnostics.ProcessStartInfo]::new($cadExe)
$cadStart.WorkingDirectory=Split-Path -Parent $cadExe
$cadStart.UseShellExecute=$false
$cadStart.CreateNoWindow=$true
$cadStart.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
$cadStart.ArgumentList.Add('--m6-benchmark')
$cadStart.ArgumentList.Add(($cadInputs -join '|'))
$cadStart.ArgumentList.Add($cadOutput)
$cadStart.ArgumentList.Add($Cycles.ToString())
$cadRun=[Diagnostics.Process]::Start($cadStart)
try {
    $cadDeadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while(!$cadRun.WaitForExit(1000)) {
        if([DateTime]::UtcNow -gt $cadDeadline){$cadRun.Kill($true);throw 'Owned M6 benchmark process timed out.'}
    }
    $cadResult=Join-Path $cadOutput 'result.txt'
    if(!(Test-Path -LiteralPath $cadResult)){throw "No benchmark result; exit $($cadRun.ExitCode)"}
    Get-Content -LiteralPath $cadResult
    if($cadRun.ExitCode -ne 0 -or !(Get-Content -LiteralPath $cadResult -Raw).StartsWith('PASS:')){throw 'M6 benchmark failed.'}
    $cadHardware=@{
        recordedUtc=[DateTime]::UtcNow.ToString('O')
        cpu=@(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors)
        gpu=@(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,CurrentHorizontalResolution,CurrentVerticalResolution)
        physicalMemoryBytes=(Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
    }
    $cadHardware | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $cadOutput 'hardware.json') -Encoding utf8
    Write-Output "Evidence: $cadOutput"
}
finally{$cadRun.Dispose()}
