[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory,[switch]$IncludeSmoke)
$ErrorActionPreference='Stop'
$cadRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$cadPublish=(Resolve-Path -LiteralPath $PublishDirectory).Path
$cadStamp=Get-Date -Format 'yyyyMMdd-HHmmss'
function Start-CadDiagnostic([string[]]$Arguments){
    $cadInfo=[Diagnostics.ProcessStartInfo]::new()
    $cadInfo.FileName=Join-Path $cadPublish 'Cadoryx.wpf.exe'
    $cadInfo.WorkingDirectory=$cadPublish
    $cadInfo.UseShellExecute=$false
    $cadInfo.CreateNoWindow=$true
    $cadInfo.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    foreach($cadArgument in $Arguments){$cadInfo.ArgumentList.Add($cadArgument)}
    $cadInfo.Environment['PATH']="$env:WINDIR\System32;$env:WINDIR;$env:ProgramFiles\dotnet"
    @($cadInfo.Environment.Keys) | Where-Object {$_ -match '^(CSF_|CASROOT|OCCT|OCCTSHARP)'} | ForEach-Object {$cadInfo.Environment.Remove($_) | Out-Null}
    [Diagnostics.Process]::Start($cadInfo)
}
function Complete-CadDiagnostic($Process,[string]$Directory,[int]$Timeout){
    try{
        if(!$Process.WaitForExit($Timeout)){throw "Diagnostic timeout: $Directory"}
        $cadResult=Get-Content -LiteralPath (Join-Path $Directory 'result.txt') -Raw
        if($Process.ExitCode -ne 0 -or !$cadResult.StartsWith('PASS:')){throw $cadResult}
        if((Get-Item -LiteralPath (Join-Path $Directory 'bindings.log')).Length){throw 'Binding errors recorded.'}
        Write-Output $cadResult
        Write-Output "Evidence: $Directory"
    }finally{
        if(!$Process.HasExited){$Process.Kill();$null=$Process.WaitForExit(10000)}
        $Process.Dispose()
    }
}
if($IncludeSmoke){
    $cadSmoke=Join-Path $cadRoot "artifacts/smoke-$cadStamp"
    $cadRun=Start-CadDiagnostic @('--smoke',$cadSmoke)
    Complete-CadDiagnostic $cadRun $cadSmoke 60000
}
$cadWindow=Join-Path $cadRoot "artifacts/window-smoke-$cadStamp"
$cadRun=Start-CadDiagnostic @('--window-smoke',$cadWindow,(Join-Path $cadRoot 'Cadoryx.Tests/Fixtures/Exchange'))
Complete-CadDiagnostic $cadRun $cadWindow 45000
$cadRecovery=Join-Path $cadRoot "artifacts/recovery-smoke-$cadStamp"
$cadSeed=Start-CadDiagnostic @('--recovery-seed',$cadRecovery)
try{
    $cadTimer=[Diagnostics.Stopwatch]::StartNew()
    while(!(Test-Path -LiteralPath (Join-Path $cadRecovery 'seed.ready'))){
        if($cadSeed.HasExited){throw "Recovery seed exited: $(Get-Content (Join-Path $cadRecovery 'seed-result.txt') -Raw)"}
        if($cadTimer.Elapsed.TotalSeconds -gt 55){throw 'Recovery production timer timed out.'}
        Start-Sleep -Milliseconds 200
    }
    $cadSeed.Kill()
    if(!$cadSeed.WaitForExit(10000)){throw 'Recovery seed did not terminate.'}
    if((Get-Item -LiteralPath (Join-Path $cadRecovery 'seed-bindings.log')).Length){throw 'Recovery seed binding errors.'}
}finally{
    if(!$cadSeed.HasExited){$cadSeed.Kill();$null=$cadSeed.WaitForExit(10000)}
    $cadSeed.Dispose()
}
$cadRestart=Start-CadDiagnostic @('--recovery-verify',$cadRecovery)
Complete-CadDiagnostic $cadRestart $cadRecovery 30000
