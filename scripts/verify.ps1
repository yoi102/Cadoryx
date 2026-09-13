param([switch]$PublishSmoke, [switch]$RecoverySmoke, [switch]$WindowSmoke, [switch]$SketchSmoke)
$ErrorActionPreference = 'Stop'
$cadRoot = Split-Path -Parent $PSScriptRoot
Push-Location $cadRoot
try {
    dotnet restore Cadoryx.slnx --configfile (Join-Path $cadRoot 'NuGet.Config') --locked-mode --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    dotnet build Cadoryx.slnx -c Release --no-restore --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    dotnet test Cadoryx.Tests -c Release --no-build --no-restore --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    if ($PublishSmoke -or $RecoverySmoke -or $WindowSmoke -or $SketchSmoke) {
        $cadStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $cadPublish = Join-Path $cadRoot "artifacts\publish\$cadStamp"
        $cadSmoke = Join-Path $cadRoot "artifacts\smoke-$cadStamp"
        dotnet publish Cadoryx.wpf/Cadoryx.wpf.csproj -c Release --self-contained false --no-restore -o $cadPublish --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        foreach ($cadFile in @('occt\OcctSharp.Native.dll','runtime-baseline.json','licenses\OcctSharp.Native\THIRD_PARTY_NOTICES.md','MathNet.Numerics.dll','licenses\MathNet.Numerics\LICENSE.md')) {
            if (!(Test-Path -LiteralPath (Join-Path $cadPublish $cadFile))) { throw "Missing publish asset: $cadFile" }
        }
        $cadStart = [System.Diagnostics.ProcessStartInfo]::new()
        $cadStart.FileName = Join-Path $cadPublish 'Cadoryx.wpf.exe'
        $cadStart.WorkingDirectory = $cadPublish
        $cadStart.UseShellExecute = $false
        $cadStart.CreateNoWindow = $true
        $cadStart.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
        $cadStart.Arguments = '--smoke "' + $cadSmoke + '"'
        $cadStart.EnvironmentVariables['PATH'] = "$env:WINDIR\System32;$env:WINDIR;$env:ProgramFiles\dotnet"
        @($cadStart.EnvironmentVariables.Keys) | Where-Object { $_ -match '^(CSF_|CASROOT|OCCT|OCCTSHARP)' } | ForEach-Object { $cadStart.EnvironmentVariables.Remove($_) }
        $cadRun = [System.Diagnostics.Process]::Start($cadStart)
        try {
            if (!$cadRun.WaitForExit(60000)) { $cadRun.Kill(); throw 'Desktop smoke timed out.' }
            $cadResult = Join-Path $cadSmoke 'result.txt'
            if (!(Test-Path -LiteralPath $cadResult)) { throw "No smoke result (exit $($cadRun.ExitCode))." }
            Get-Content -LiteralPath $cadResult
            if ($cadRun.ExitCode -ne 0 -or !(Get-Content -LiteralPath $cadResult -Raw).StartsWith('PASS:')) { throw 'Desktop smoke failed.' }
            if ((Get-Item -LiteralPath (Join-Path $cadSmoke 'bindings.log')).Length -gt 0) { throw 'WPF binding errors recorded.' }
            Write-Output "Verified publish: $cadPublish"
            Write-Output "Evidence: $cadSmoke"
        }
        finally { $cadRun.Dispose() }
        if ($WindowSmoke) {
            $cadWindowSmoke = Join-Path $cadRoot "artifacts\window-smoke-$cadStamp"
            $cadFixtures = Join-Path $cadRoot 'Cadoryx.Tests\Fixtures\Exchange'
            $cadStart.Arguments = '--window-smoke "' + $cadWindowSmoke + '" "' + $cadFixtures + '"'
            $cadWindowRun = [System.Diagnostics.Process]::Start($cadStart)
            try {
                if (!$cadWindowRun.WaitForExit(45000)) { $cadWindowRun.Kill(); throw 'Window smoke timed out.' }
                $cadWindowResult = Join-Path $cadWindowSmoke 'result.txt'
                if (!(Test-Path -LiteralPath $cadWindowResult)) { throw "No window result (exit $($cadWindowRun.ExitCode))." }
                Get-Content -LiteralPath $cadWindowResult
                if ($cadWindowRun.ExitCode -ne 0 -or !(Get-Content -LiteralPath $cadWindowResult -Raw).StartsWith('PASS:')) { throw 'Window smoke failed.' }
                if ((Get-Item -LiteralPath (Join-Path $cadWindowSmoke 'bindings.log')).Length -gt 0) { throw 'Window binding errors recorded.' }
                Write-Output "Window evidence: $cadWindowSmoke"
            }
            finally { $cadWindowRun.Dispose() }
        }
        if ($SketchSmoke) {
            $cadSketchSmoke = Join-Path $cadRoot "artifacts\sketch-editor-smoke-$cadStamp"
            $cadStart.Arguments = '--sketch-editor-smoke "' + $cadSketchSmoke + '"'
            $cadSketchRun = [System.Diagnostics.Process]::Start($cadStart)
            try {
                if (!$cadSketchRun.WaitForExit(45000)) { $cadSketchRun.Kill(); throw 'Sketch editor smoke timed out.' }
                $cadSketchResult = Join-Path $cadSketchSmoke 'result.txt'
                if (!(Test-Path -LiteralPath $cadSketchResult)) { throw "No sketch editor result (exit $($cadSketchRun.ExitCode))." }
                Get-Content -LiteralPath $cadSketchResult
                if ($cadSketchRun.ExitCode -ne 0 -or !(Get-Content -LiteralPath $cadSketchResult -Raw).StartsWith('PASS:')) { throw 'Sketch editor smoke failed.' }
                if ((Get-Item -LiteralPath (Join-Path $cadSketchSmoke 'bindings.log')).Length -gt 0) { throw 'Sketch editor binding errors recorded.' }
                Write-Output "Sketch editor evidence: $cadSketchSmoke"
            }
            finally { $cadSketchRun.Dispose() }
        }
        if ($RecoverySmoke) {
            $cadRecovery = Join-Path $cadRoot "artifacts\recovery-smoke-$cadStamp"
            $cadStart.Arguments = '--recovery-seed "' + $cadRecovery + '"'
            $cadSeed = [System.Diagnostics.Process]::Start($cadStart)
            try {
                $cadDeadline = [System.Diagnostics.Stopwatch]::StartNew()
                $cadReady = Join-Path $cadRecovery 'seed.ready'
                while (!(Test-Path -LiteralPath $cadReady)) {
                    if ($cadSeed.HasExited) {
                        $cadSeedResult = Join-Path $cadRecovery 'seed-result.txt'
                        if (Test-Path -LiteralPath $cadSeedResult) { Get-Content -LiteralPath $cadSeedResult }
                        throw 'Recovery seed exited before its timer checkpoint.'
                    }
                    if ($cadDeadline.Elapsed.TotalSeconds -gt 55) { throw 'Recovery timer checkpoint timed out.' }
                    Start-Sleep -Milliseconds 200
                }
                # Terminate only the diagnostic process just started, without running normal close cleanup.
                $cadSeed.Kill()
                if (!$cadSeed.WaitForExit(10000)) { throw 'Recovery seed did not terminate.' }
                if ((Get-Item -LiteralPath (Join-Path $cadRecovery 'seed-bindings.log')).Length -gt 0) { throw 'Recovery seed binding errors recorded.' }
            }
            finally {
                if (!$cadSeed.HasExited) { $cadSeed.Kill(); $null = $cadSeed.WaitForExit(10000) }
                $cadSeed.Dispose()
            }
            $cadStart.Arguments = '--recovery-verify "' + $cadRecovery + '"'
            $cadRestart = [System.Diagnostics.Process]::Start($cadStart)
            try {
                if (!$cadRestart.WaitForExit(30000)) { $cadRestart.Kill(); throw 'Recovery restart timed out.' }
                $cadRecoveryResult = Join-Path $cadRecovery 'result.txt'
                if (!(Test-Path -LiteralPath $cadRecoveryResult)) { throw "No recovery result (exit $($cadRestart.ExitCode))." }
                Get-Content -LiteralPath $cadRecoveryResult
                if ($cadRestart.ExitCode -ne 0 -or !(Get-Content -LiteralPath $cadRecoveryResult -Raw).StartsWith('PASS:')) { throw 'Recovery smoke failed.' }
                if ((Get-Item -LiteralPath (Join-Path $cadRecovery 'bindings.log')).Length -gt 0) { throw 'Recovery UI binding errors recorded.' }
                Write-Output "Recovery evidence: $cadRecovery"
            }
            finally { $cadRestart.Dispose() }
        }
    }
}
finally { Pop-Location }
