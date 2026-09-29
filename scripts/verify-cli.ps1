param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$cadRoot=Split-Path -Parent $PSScriptRoot
if(!$OutputDirectory){$OutputDirectory=Join-Path $cadRoot ('artifacts/cli-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
$cadOutput=[IO.Path]::GetFullPath($OutputDirectory)
$cadPublish=Join-Path $cadOutput 'publish'
New-Item -ItemType Directory -Path $cadOutput -Force | Out-Null
dotnet publish (Join-Path $cadRoot 'Cadoryx.Cli/Cadoryx.Cli.csproj') -c Release --no-restore --self-contained false -o $cadPublish --nologo -v minimal
if($LASTEXITCODE -ne 0){throw 'CLI publish failed.'}
$cadFixture=Join-Path $cadRoot 'Cadoryx.Tests/Fixtures/Storage/v2-box.cadoryx'
$script:cadCliRunIndex=0
function Invoke-CadCli([string[]]$Arguments,[int]$Expected=0) {
    $cadStart=[Diagnostics.ProcessStartInfo]::new()
    $cadStart.FileName=Join-Path $cadPublish 'Cadoryx.Cli.exe'
    $cadStart.WorkingDirectory=$cadPublish
    $cadStart.UseShellExecute=$false
    $cadStart.CreateNoWindow=$true
    $cadStart.RedirectStandardOutput=$true
    $cadStart.RedirectStandardError=$true
    $cadStart.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $cadStart.Environment['PATH']="$env:WINDIR\System32;$env:WINDIR;$env:ProgramFiles\dotnet"
    @($cadStart.Environment.Keys) | Where-Object {$_ -match '^(CSF_|CASROOT|OCCT|OCCTSHARP)'} | ForEach-Object {$cadStart.Environment.Remove($_) | Out-Null}
    foreach($cadArgument in $Arguments){$cadStart.ArgumentList.Add($cadArgument)}
    $cadRun=[Diagnostics.Process]::Start($cadStart)
    $cadStdout=$cadRun.StandardOutput.ReadToEndAsync()
    $cadStderr=$cadRun.StandardError.ReadToEndAsync()
    try {
        if(!$cadRun.WaitForExit(60000)){$cadRun.Kill();throw 'CLI timed out.'}
        $script:cadCliRunIndex++
        $cadLog=Join-Path $cadOutput ('{0:D2}-{1}.log' -f $script:cadCliRunIndex,$Arguments[0])
        ($cadStdout.GetAwaiter().GetResult()+$cadStderr.GetAwaiter().GetResult()) | Set-Content -LiteralPath $cadLog
        if($cadRun.ExitCode -ne $Expected){throw "CLI exit $($cadRun.ExitCode), expected $Expected; $Arguments"}
    } finally {$cadRun.Dispose()}
}
Invoke-CadCli @('inspect',$cadFixture,'--exact','--output',(Join-Path $cadOutput 'inspection.json'))
foreach($cadFormat in @('step','iges','stl')){
    $cadTarget=Join-Path $cadOutput "converted.$cadFormat"
    Invoke-CadCli @('convert',$cadFixture,$cadTarget)
    if((Get-Item -LiteralPath $cadTarget).Length -eq 0){throw 'Empty conversion output.'}
    Invoke-CadCli @('convert',$cadFixture,$cadTarget) 1
}
Invoke-CadCli @('inspect',(Join-Path $cadOutput 'converted.step'),'--exact','--output',(Join-Path $cadOutput 'reimport.json'))
Invoke-CadCli @('bom',$cadFixture,'--output',(Join-Path $cadOutput 'bom.csv'))
Invoke-CadCli @('report',$cadFixture,'--output',(Join-Path $cadOutput 'review.html'))
$cadDelivery=Join-Path $cadOutput 'delivery.zip'
Invoke-CadCli @('deliver',$cadFixture,'--output',$cadDelivery,'--iges','--stl')
Invoke-CadCli @('verify-delivery',$cadDelivery)
$cadDeliveryHash=(Get-FileHash -LiteralPath $cadDelivery -Algorithm SHA256).Hash
Invoke-CadCli @('deliver',$cadFixture,'--output',$cadDelivery) 1
if((Get-FileHash -LiteralPath $cadDelivery -Algorithm SHA256).Hash -ne $cadDeliveryHash){throw 'Refused delivery overwrite changed the existing package.'}
'PASS: published BOM, HTML, seven-file delivery, independent archive verification and overwrite protection.' | Set-Content (Join-Path $cadOutput 'delivery-result.txt')
Invoke-CadCli @('benchmark',$cadFixture,'--iterations','2','--output',(Join-Path $cadOutput 'benchmark-disk.json'))
Invoke-CadCli @('benchmark',$cadFixture,'--memory','--iterations','2','--output',(Join-Path $cadOutput 'benchmark-memory.json'))
$cadBenchmark=Get-Content -LiteralPath (Join-Path $cadOutput 'benchmark-disk.json') -Raw | ConvertFrom-Json
if($cadBenchmark.samples.Count -ne 2 -or !$cadBenchmark.sourceSha256){throw 'Incomplete benchmark report.'}
$cadInspection=Get-Content -LiteralPath (Join-Path $cadOutput 'inspection.json') -Raw | ConvertFrom-Json
if(!$cadInspection.exact.bodies.Count){throw 'Missing exact inspection report.'}
$cadCacheRoot=Join-Path $cadOutput 'lifetime-cache'
function Start-CadCacheSeed([string]$Ready) {
    $cadSeedStart=[Diagnostics.ProcessStartInfo]::new()
    $cadSeedStart.FileName=Join-Path $cadPublish 'Cadoryx.Cli.exe'
    $cadSeedStart.UseShellExecute=$false
    $cadSeedStart.CreateNoWindow=$true
    $cadSeedStart.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    foreach($cadArg in @('--asset-cache-seed',$cadCacheRoot,$Ready)){$cadSeedStart.ArgumentList.Add($cadArg)}
    return [Diagnostics.Process]::Start($cadSeedStart)
}
function Wait-CadCacheSeed($Process,[string]$Ready) {
    $cadDeadline=[Diagnostics.Stopwatch]::StartNew()
    while($true) {
        if(Test-Path -LiteralPath $Ready){
            try{
                $cadPath=[IO.File]::ReadAllText($Ready).Trim()
                if($cadPath){return $cadPath}
            }
            catch [IO.IOException] {
                # The seed creates the marker before releasing its exclusive write handle.
            }
        }
        if($Process.HasExited -or $cadDeadline.Elapsed.TotalSeconds -gt 15){throw 'Cache seed failed to hold its live payload.'}
        Start-Sleep -Milliseconds 100
    }
}
$cadSeedReady=Join-Path $cadOutput 'cache-seed.ready'
$cadCacheSeed=Start-CadCacheSeed $cadSeedReady
try {
    $cadLiveDirectory=Wait-CadCacheSeed $cadCacheSeed $cadSeedReady
    Invoke-CadCli @('cache-clean',$cadCacheRoot)
    if(!(Test-Path -LiteralPath $cadLiveDirectory)){throw 'Cleaner removed an active cache.'}
    $cadCacheSeed.Kill();$null=$cadCacheSeed.WaitForExit(10000)
} finally {if(!$cadCacheSeed.HasExited){$cadCacheSeed.Kill();$null=$cadCacheSeed.WaitForExit(10000)};$cadCacheSeed.Dispose()}
$cadRestartReady=Join-Path $cadOutput 'cache-restart.ready'
$cadCacheRestart=Start-CadCacheSeed $cadRestartReady
try {
    $cadRestartDirectory=Wait-CadCacheSeed $cadCacheRestart $cadRestartReady
    if(Test-Path -LiteralPath $cadLiveDirectory){throw 'Restart did not reclaim the terminated process cache.'}
    Invoke-CadCli @('cache-clean',$cadCacheRoot)
    if(!(Test-Path -LiteralPath $cadRestartDirectory)){throw 'Cleaner removed the restarted active cache.'}
} finally {if(!$cadCacheRestart.HasExited){$cadCacheRestart.Kill();$null=$cadCacheRestart.WaitForExit(10000)};$cadCacheRestart.Dispose()}
Invoke-CadCli @('cache-clean',$cadCacheRoot)
if(Test-Path -LiteralPath $cadRestartDirectory){throw 'Final orphan cleanup failed.'}
'PASS: live-process exclusion, forced termination, automatic restart cleanup, final explicit cleanup.' | Set-Content (Join-Path $cadOutput 'cache-lifetime-result.txt')
'PASS: standalone CLI, exact report, STEP/IGES/STL conversion, overwrite protection, STEP reimport, disk/memory benchmark reports, real-process cache lifecycle.' | Set-Content (Join-Path $cadOutput 'result.txt')
Write-Output "CLI evidence: $cadOutput"
