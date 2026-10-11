param([switch]$PublishSmoke, [switch]$RecoverySmoke, [switch]$WindowSmoke, [switch]$SketchSmoke, [switch]$DrawingSmoke, [switch]$B3SketchSmoke, [switch]$B4SketchSmoke, [switch]$B5SketchSmoke, [switch]$B6SketchSmoke, [switch]$B7SketchSmoke, [switch]$B8RevolveSmoke, [switch]$B9CurveSmoke, [switch]$B10RegionSmoke, [switch]$B11ProjectionSmoke, [switch]$E1HistorySmoke, [switch]$E2AssemblySmoke, [switch]$C1ParameterSmoke, [switch]$C2DatumSmoke, [switch]$C3RepetitionSmoke, [switch]$C3AssemblySmoke, [switch]$C4HoleSmoke, [switch]$D1ExtrusionSmoke, [switch]$D2GuidedSmoke, [switch]$D3FaceFeatureSmoke, [switch]$AutomationButtonInput)
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
    if ($PublishSmoke) { & (Join-Path $PSScriptRoot 'verify-cli.ps1') }
    if ($PublishSmoke -or $RecoverySmoke -or $WindowSmoke -or $SketchSmoke -or $DrawingSmoke -or $B3SketchSmoke -or $B4SketchSmoke -or $B5SketchSmoke -or $B6SketchSmoke -or $B7SketchSmoke -or $B8RevolveSmoke -or $B9CurveSmoke -or $B10RegionSmoke -or $B11ProjectionSmoke -or $E1HistorySmoke -or $E2AssemblySmoke -or $C1ParameterSmoke -or $C2DatumSmoke -or $C3RepetitionSmoke -or $C3AssemblySmoke -or $C4HoleSmoke -or $D1ExtrusionSmoke -or $D2GuidedSmoke -or $D3FaceFeatureSmoke) {
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
        if ($AutomationButtonInput) { $cadStart.EnvironmentVariables['CADORYX_SMOKE_BUTTON_INPUT']='automation-invoke'; Write-Output 'Button gate: WPF Automation Invoke/Toggle; desktop pointer hit testing NOT VERIFIED.' } else { $cadStart.EnvironmentVariables.Remove('CADORYX_SMOKE_BUTTON_INPUT') }
        $cadStart.CreateNoWindow = $true
        $cadStart.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
        $cadStart.Arguments = '--smoke "' + $cadSmoke + '"'
        $cadStart.EnvironmentVariables['PATH'] = "$env:WINDIR\System32;$env:WINDIR;$env:ProgramFiles\dotnet"
        @($cadStart.EnvironmentVariables.Keys) | Where-Object { $_ -match '^(CSF_|CASROOT|OCCT|OCCTSHARP)' } | ForEach-Object { $cadStart.EnvironmentVariables.Remove($_) }
        $cadRun = [System.Diagnostics.Process]::Start($cadStart)
        try {
            if (!$cadRun.WaitForExit(240000)) { $cadRun.Kill(); throw 'Desktop smoke timed out.' }
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
                if (!$cadWindowRun.WaitForExit(240000)) { $cadWindowRun.Kill(); throw 'Window smoke timed out.' }
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
                if (!$cadSketchRun.WaitForExit(60000)) { $cadSketchRun.Kill(); throw 'Sketch editor smoke timed out.' }
                $cadSketchResult = Join-Path $cadSketchSmoke 'result.txt'
                if (!(Test-Path -LiteralPath $cadSketchResult)) { throw "No sketch editor result (exit $($cadSketchRun.ExitCode))." }
                Get-Content -LiteralPath $cadSketchResult
                if ($cadSketchRun.ExitCode -ne 0 -or !(Get-Content -LiteralPath $cadSketchResult -Raw).StartsWith('PASS:')) { throw 'Sketch editor smoke failed.' }
                if ((Get-Item -LiteralPath (Join-Path $cadSketchSmoke 'bindings.log')).Length -gt 0) { throw 'Sketch editor binding errors recorded.' }
                Write-Output "Sketch editor evidence: $cadSketchSmoke"
            }
            finally { $cadSketchRun.Dispose() }
        }
        if ($B3SketchSmoke) {
            $cadB3Smoke = Join-Path $cadRoot "artifacts\b3-sketch-smoke-$cadStamp"
            $cadStart.Arguments = '--b3-sketch-smoke "' + $cadB3Smoke + '"'
            $cadB3Run = [System.Diagnostics.Process]::Start($cadStart)
            try {
                if (!$cadB3Run.WaitForExit(45000)) { $cadB3Run.Kill(); throw 'B3 sketch smoke timed out.' }
                $cadB3Result = Join-Path $cadB3Smoke 'result.txt'
                if (!(Test-Path -LiteralPath $cadB3Result)) { throw "No B3 sketch result (exit $($cadB3Run.ExitCode))." }
                Get-Content -LiteralPath $cadB3Result
                if ($cadB3Run.ExitCode -ne 0 -or !(Get-Content -LiteralPath $cadB3Result -Raw).StartsWith('PASS:')) { throw 'B3 sketch smoke failed.' }
                if ((Get-Item -LiteralPath (Join-Path $cadB3Smoke 'bindings.log')).Length -gt 0) { throw 'B3 sketch binding errors recorded.' }
                Write-Output "B3 sketch evidence: $cadB3Smoke"
            }
            finally { $cadB3Run.Dispose() }
        }
        if ($B4SketchSmoke) {
            $cadB4Smoke = Join-Path $cadRoot "artifacts\b4-sketch-smoke-$cadStamp"
            $cadStart.Arguments = '--b4-sketch-smoke "' + $cadB4Smoke + '"'
            $cadB4Run = [System.Diagnostics.Process]::Start($cadStart)
            try {
                if (!$cadB4Run.WaitForExit(45000)) { $cadB4Run.Kill(); throw 'B4 sketch smoke timed out.' }
                $cadB4Result = Join-Path $cadB4Smoke 'result.txt'
                if (!(Test-Path -LiteralPath $cadB4Result)) { throw "No B4 sketch result (exit $($cadB4Run.ExitCode))." }
                Get-Content -LiteralPath $cadB4Result
                if ($cadB4Run.ExitCode -ne 0 -or !(Get-Content -LiteralPath $cadB4Result -Raw).StartsWith('PASS:')) { throw 'B4 sketch smoke failed.' }
                if ((Get-Item -LiteralPath (Join-Path $cadB4Smoke 'bindings.log')).Length -gt 0) { throw 'B4 sketch binding errors recorded.' }
                Write-Output "B4 sketch evidence: $cadB4Smoke"
            }
            finally { $cadB4Run.Dispose() }
        }
        if ($B5SketchSmoke) {
            $cadB5Smoke = Join-Path $cadRoot "artifacts\b5-sketch-smoke-$cadStamp"
            $cadStart.Arguments = '--b5-sketch-smoke "' + $cadB5Smoke + '"'
            $cadB5Run = [System.Diagnostics.Process]::Start($cadStart)
            try {
                if (!$cadB5Run.WaitForExit(45000)) { $cadB5Run.Kill(); throw 'B5 sketch smoke timed out.' }
                $cadB5Result = Join-Path $cadB5Smoke 'result.txt'
                if (!(Test-Path -LiteralPath $cadB5Result)) { throw "No B5 sketch result (exit $($cadB5Run.ExitCode))." }
                Get-Content -LiteralPath $cadB5Result
                if ($cadB5Run.ExitCode -ne 0 -or !(Get-Content -LiteralPath $cadB5Result -Raw).StartsWith('PASS:')) { throw 'B5 sketch smoke failed.' }
                if ((Get-Item -LiteralPath (Join-Path $cadB5Smoke 'bindings.log')).Length -gt 0) { throw 'B5 sketch binding errors recorded.' }
                Write-Output "B5 sketch evidence: $cadB5Smoke"
            }
            finally { $cadB5Run.Dispose() }
        }
        if ($B6SketchSmoke) {
            $cadB6Smoke = Join-Path $cadRoot "artifacts\b6-sketch-smoke-$cadStamp"
            $cadStart.Arguments = '--b6-sketch-smoke "' + $cadB6Smoke + '"'
            $cadB6Run = [System.Diagnostics.Process]::Start($cadStart)
            try {
                if (!$cadB6Run.WaitForExit(60000)) { $cadB6Run.Kill(); throw 'B6 sketch smoke timed out.' }
                $cadB6Result = Join-Path $cadB6Smoke 'result.txt'
                if (!(Test-Path -LiteralPath $cadB6Result)) { throw "No B6 sketch result (exit $($cadB6Run.ExitCode))." }
                Get-Content -LiteralPath $cadB6Result
                if ($cadB6Run.ExitCode -ne 0 -or !(Get-Content -LiteralPath $cadB6Result -Raw).StartsWith('PASS:')) { throw 'B6 sketch smoke failed.' }
                if ((Get-Item -LiteralPath (Join-Path $cadB6Smoke 'bindings.log')).Length -gt 0) { throw 'B6 sketch binding errors recorded.' }
                Write-Output "B6 sketch evidence: $cadB6Smoke"
            }
            finally { $cadB6Run.Dispose() }
        }
        if ($B7SketchSmoke) {
            $cadB7Smoke = Join-Path $cadRoot "artifacts\b7-sketch-smoke-$cadStamp"
            $cadStart.Arguments = '--b7-sketch-smoke "' + $cadB7Smoke + '"'
            $cadB7Run = [System.Diagnostics.Process]::Start($cadStart)
            try {
                if (!$cadB7Run.WaitForExit(60000)) { $cadB7Run.Kill(); throw 'B7 sketch smoke timed out.' }
                $cadB7Result = Join-Path $cadB7Smoke 'result.txt'
                if (!(Test-Path -LiteralPath $cadB7Result)) { throw "No B7 sketch result (exit $($cadB7Run.ExitCode))." }
                Get-Content -LiteralPath $cadB7Result
                if ($cadB7Run.ExitCode -ne 0 -or !(Get-Content -LiteralPath $cadB7Result -Raw).StartsWith('PASS:')) { throw 'B7 sketch smoke failed.' }
                if ((Get-Item -LiteralPath (Join-Path $cadB7Smoke 'bindings.log')).Length -gt 0) { throw 'B7 sketch binding errors recorded.' }
                Write-Output "B7 sketch evidence: $cadB7Smoke"
            }
            finally { $cadB7Run.Dispose() }
        }
        if ($B8RevolveSmoke) {
            & (Join-Path $PSScriptRoot 'verify-b8-revolve.ps1') -Publish $cadPublish -OutputDirectory (Join-Path $cadRoot "artifacts\b8-revolve-smoke-$cadStamp")
        }
        if ($B9CurveSmoke) {
            & (Join-Path $PSScriptRoot 'verify-b9-curves.ps1') -Publish $cadPublish -OutputDirectory (Join-Path $cadRoot "artifacts\b9-curve-smoke-$cadStamp")
        }
        if ($B10RegionSmoke) {
            & (Join-Path $PSScriptRoot 'verify-b10-regions.ps1') -Publish $cadPublish -OutputDirectory (Join-Path $cadRoot "artifacts\b10-region-smoke-$cadStamp")
        }
        if ($B11ProjectionSmoke) {
            & (Join-Path $PSScriptRoot 'verify-b11-projections.ps1') -Publish $cadPublish -OutputDirectory (Join-Path $cadRoot "artifacts\b11-projection-smoke-$cadStamp")
        }
        if ($E1HistorySmoke) {
            & (Join-Path $PSScriptRoot 'verify-e1-history.ps1') -Publish $cadPublish -OutputDirectory (Join-Path $cadRoot "artifacts\e1-history-smoke-$cadStamp")
        }
        if ($E2AssemblySmoke) {
            & (Join-Path $PSScriptRoot 'verify-e2-assembly.ps1') -Publish $cadPublish -OutputDirectory (Join-Path $cadRoot "artifacts\e2-assembly-smoke-$cadStamp")
        }
        if ($C1ParameterSmoke) {
            $cadC1Smoke = Join-Path $cadRoot "artifacts\c1-parameter-smoke-$cadStamp"
            $cadStart.Arguments = '--c1-parameter-smoke "' + $cadC1Smoke + '"'
            $cadC1Run = [System.Diagnostics.Process]::Start($cadStart)
            try {
                if (!$cadC1Run.WaitForExit(60000)) { $cadC1Run.Kill(); throw 'C1 parameter smoke timed out.' }
                $cadC1Result = Join-Path $cadC1Smoke 'result.txt'
                if (!(Test-Path -LiteralPath $cadC1Result)) { throw "No C1 parameter result (exit $($cadC1Run.ExitCode))." }
                Get-Content -LiteralPath $cadC1Result
                if ($cadC1Run.ExitCode -ne 0 -or !(Get-Content -LiteralPath $cadC1Result -Raw).StartsWith('PASS:')) { throw 'C1 parameter smoke failed.' }
                if ((Get-Item -LiteralPath (Join-Path $cadC1Smoke 'bindings.log')).Length -gt 0) { throw 'C1 parameter binding errors recorded.' }
                Write-Output "C1 parameter evidence: $cadC1Smoke"
            }
            finally { $cadC1Run.Dispose() }
        }
        if ($C2DatumSmoke) {
            $cadC2Smoke = Join-Path $cadRoot "artifacts\c2-datum-smoke-$cadStamp"
            $cadStart.Arguments = '--c2-datum-smoke "' + $cadC2Smoke + '"'
            $cadC2Run = [System.Diagnostics.Process]::Start($cadStart)
            try {
                $cadC2Deadline=[Diagnostics.Stopwatch]::StartNew()
                while (!$cadC2Run.WaitForExit(1000)) {
                    if ($cadC2Deadline.Elapsed.TotalSeconds -gt 120) { $cadC2Run.Kill(); throw 'C2 datum smoke timed out.' }
                }
                $cadC2Result = Join-Path $cadC2Smoke 'result.txt'
                if (!(Test-Path -LiteralPath $cadC2Result)) { throw "No C2 datum result (exit $($cadC2Run.ExitCode))." }
                Get-Content -LiteralPath $cadC2Result
                if ($cadC2Run.ExitCode -ne 0 -or !(Get-Content -LiteralPath $cadC2Result -Raw).StartsWith('PASS:')) { throw 'C2 datum smoke failed.' }
                if ((Get-Item -LiteralPath (Join-Path $cadC2Smoke 'bindings.log')).Length -gt 0) { throw 'C2 datum binding errors recorded.' }
                foreach ($cadLanguage in @('en-US','zh-CN','ja-JP')) {
                    foreach ($cadKind in @('datums','extended-plane','extended-axis','extended-point','extended-native','extended-circle','extended-cylinder-axis','revolve-axis')) {
                        if (!(Test-Path -LiteralPath (Join-Path $cadC2Smoke "$cadKind-$cadLanguage.png"))) { throw "Missing C2 screenshot: $cadKind-$cadLanguage" }
                    }
                }
                foreach ($cadFile in @('extended-stale-axis-reselected.png','accepted-extended.cadoryx')) {
                    if (!(Test-Path -LiteralPath (Join-Path $cadC2Smoke $cadFile))) { throw "Missing extended C2 evidence: $cadFile" }
                }
                Write-Output "C2 datum evidence: $cadC2Smoke"
            }
            finally { $cadC2Run.Dispose() }
        }
        if ($C3RepetitionSmoke) {
            $cadC3Smoke = Join-Path $cadRoot "artifacts\c3-repetition-smoke-$cadStamp"
            $cadStart.Arguments = '--c3-repetition-smoke "' + $cadC3Smoke + '"'
            $cadC3Run = [System.Diagnostics.Process]::Start($cadStart)
            try {
                if (!$cadC3Run.WaitForExit(180000)) { $cadC3Run.Kill(); throw 'C3 repetition smoke timed out.' }
                $cadC3Result = Join-Path $cadC3Smoke 'result.txt'
                if (!(Test-Path -LiteralPath $cadC3Result)) { throw "No C3 repetition result (exit $($cadC3Run.ExitCode))." }
                Get-Content -LiteralPath $cadC3Result
                if ($cadC3Run.ExitCode -ne 0 -or !(Get-Content -LiteralPath $cadC3Result -Raw).StartsWith('PASS:')) { throw 'C3 repetition smoke failed.' }
                if ((Get-Item -LiteralPath (Join-Path $cadC3Smoke 'bindings.log')).Length -gt 0) { throw 'C3 repetition binding errors recorded.' }
                foreach ($cadC3File in @('accepted.cadoryx','accepted-hole-replay.cadoryx','repetition-en-US.png','repetition-zh-CN.png','repetition-ja-JP.png','hole-replay-en-US.png','hole-replay-zh-CN.png','hole-replay-ja-JP.png','hole-replay-frame-en-US.png','hole-replay-frame-zh-CN.png','hole-replay-frame-ja-JP.png')) {
                    if (!(Test-Path -LiteralPath (Join-Path $cadC3Smoke $cadC3File))) { throw "Missing C3 repetition evidence: $cadC3File" }
                }
                if (!(Test-Path -LiteralPath (Join-Path $cadC3Smoke 'accepted-extrusion-replay.cadoryx'))) { throw 'Missing C3 accepted material replay document.' }
                foreach ($cadC3Mode in @('add','remove')) { foreach ($cadC3Language in @('en-US','zh-CN','ja-JP')) {
                    foreach ($cadC3Prefix in @('extrusion-replay','extrusion-replay-frame')) {
                        $cadC3File="$cadC3Prefix-$cadC3Mode-$cadC3Language.png"
                        if (!(Test-Path -LiteralPath (Join-Path $cadC3Smoke $cadC3File))) { throw "Missing C3 material replay evidence: $cadC3File" }
                    }
                } }
                Write-Output "C3 repetition evidence: $cadC3Smoke"
            }
            finally { $cadC3Run.Dispose() }
        }
        if ($C3AssemblySmoke) {
            $cadC3ArraySmoke = Join-Path $cadRoot "artifacts\c3-assembly-smoke-$cadStamp"
            $cadStart.Arguments = '--c3-assembly-smoke "' + $cadC3ArraySmoke + '"'
            $cadC3ArrayRun = [System.Diagnostics.Process]::Start($cadStart)
            try {
                if (!$cadC3ArrayRun.WaitForExit(180000)) { $cadC3ArrayRun.Kill(); throw 'C3 assembly smoke timed out.' }
                $cadC3ArrayResult = Join-Path $cadC3ArraySmoke 'result.txt'
                if (!(Test-Path -LiteralPath $cadC3ArrayResult)) { throw "No C3 assembly result (exit $($cadC3ArrayRun.ExitCode))." }
                Get-Content -LiteralPath $cadC3ArrayResult
                if ($cadC3ArrayRun.ExitCode -ne 0 -or !(Get-Content -LiteralPath $cadC3ArrayResult -Raw).StartsWith('PASS:')) { throw 'C3 assembly smoke failed.' }
                if ((Get-Item -LiteralPath (Join-Path $cadC3ArraySmoke 'bindings.log')).Length -gt 0) { throw 'C3 assembly binding errors recorded.' }
                foreach ($cadArrayLanguage in @('en-US','zh-CN','ja-JP')) { foreach ($cadArrayKind in @('linear','circular','mirror','detach')) {
                    if (!(Test-Path -LiteralPath (Join-Path $cadC3ArraySmoke "assembly-array-$cadArrayKind-$cadArrayLanguage.png"))) { throw 'Missing assembly array screenshot.' }
                } }
                if (!(Test-Path -LiteralPath (Join-Path $cadC3ArraySmoke 'accepted-assembly-array.cadoryx'))) { throw 'Missing accepted assembly array document.' }
                if (!(Test-Path -LiteralPath (Join-Path $cadC3ArraySmoke 'accepted-assembly-mirror.cadoryx'))) { throw 'Missing accepted assembly mirror document.' }
                Write-Output "C3 assembly evidence: $cadC3ArraySmoke"
            }
            finally { $cadC3ArrayRun.Dispose() }
        }
        if ($C4HoleSmoke) {
            $cadC4Smoke = Join-Path $cadRoot "artifacts\c4-hole-smoke-$cadStamp"
            $cadStart.Arguments = '--c4-hole-smoke "' + $cadC4Smoke + '"'
            $cadC4Run = [System.Diagnostics.Process]::Start($cadStart)
            try {
                if (!$cadC4Run.WaitForExit(60000)) { $cadC4Run.Kill(); throw 'C4 hole smoke timed out.' }
                $cadC4Result = Join-Path $cadC4Smoke 'result.txt'
                if (!(Test-Path -LiteralPath $cadC4Result)) { throw "No C4 hole result (exit $($cadC4Run.ExitCode))." }
                Get-Content -LiteralPath $cadC4Result
                if ($cadC4Run.ExitCode -ne 0 -or !(Get-Content -LiteralPath $cadC4Result -Raw).StartsWith('PASS:')) { throw 'C4 hole smoke failed.' }
                if ((Get-Item -LiteralPath (Join-Path $cadC4Smoke 'bindings.log')).Length -gt 0) { throw 'C4 hole binding errors recorded.' }
                Write-Output "C4 hole evidence: $cadC4Smoke"
            }
            finally { $cadC4Run.Dispose() }
        }
        if ($D1ExtrusionSmoke) {
            $cadD1Smoke = Join-Path $cadRoot "artifacts\d1-extrusion-smoke-$cadStamp"
            $cadStart.Arguments = '--d1-extrusion-smoke "' + $cadD1Smoke + '"'
            $cadD1Run = [System.Diagnostics.Process]::Start($cadStart)
            try {
                if (!$cadD1Run.WaitForExit(60000)) { $cadD1Run.Kill(); throw 'D1 extrusion smoke timed out.' }
                $cadD1Result = Join-Path $cadD1Smoke 'result.txt'
                if (!(Test-Path -LiteralPath $cadD1Result)) { throw "No D1 extrusion result (exit $($cadD1Run.ExitCode))." }
                Get-Content -LiteralPath $cadD1Result
                if ($cadD1Run.ExitCode -ne 0 -or !(Get-Content -LiteralPath $cadD1Result -Raw).StartsWith('PASS:')) { throw 'D1 extrusion smoke failed.' }
                if ((Get-Item -LiteralPath (Join-Path $cadD1Smoke 'bindings.log')).Length -gt 0) { throw 'D1 extrusion binding errors recorded.' }
                Write-Output "D1 extrusion evidence: $cadD1Smoke"
            }
            finally { $cadD1Run.Dispose() }
        }
        if ($D2GuidedSmoke) {
            $cadD2Smoke = Join-Path $cadRoot "artifacts\d2-guided-smoke-$cadStamp"
            $cadStart.Arguments = '--d2-guided-smoke "' + $cadD2Smoke + '"'
            $cadD2Run = [System.Diagnostics.Process]::Start($cadStart)
            try {
                if (!$cadD2Run.WaitForExit(120000)) { $cadD2Run.Kill(); throw 'D2 guided smoke timed out.' }
                $cadD2Result = Join-Path $cadD2Smoke 'result.txt'
                if (!(Test-Path -LiteralPath $cadD2Result)) { throw "No D2 guided result (exit $($cadD2Run.ExitCode))." }
                Get-Content -LiteralPath $cadD2Result
                if ($cadD2Run.ExitCode -ne 0 -or !(Get-Content -LiteralPath $cadD2Result -Raw).StartsWith('PASS:')) { throw 'D2 guided smoke failed.' }
                if ((Get-Item -LiteralPath (Join-Path $cadD2Smoke 'bindings.log')).Length -gt 0) { throw 'D2 guided binding errors recorded.' }
                foreach ($cadLanguage in @('en-US','zh-CN','ja-JP')) {
                    foreach ($cadKind in @('sweep','loft')) {
                        if (!(Test-Path -LiteralPath (Join-Path $cadD2Smoke "$cadKind-$cadLanguage.png"))) { throw "Missing D2 screenshot: $cadKind-$cadLanguage" }
                    }
                }
                Write-Output "D2 guided evidence: $cadD2Smoke"
            }
            finally { $cadD2Run.Dispose() }
        }
        if ($D3FaceFeatureSmoke) {
            $cadD3Smoke = Join-Path $cadRoot "artifacts\d3-face-feature-smoke-$cadStamp"
            $cadStart.Arguments = '--d3-face-feature-smoke "' + $cadD3Smoke + '"'
            $cadD3Run = [System.Diagnostics.Process]::Start($cadStart)
            try {
                if (!$cadD3Run.WaitForExit(120000)) { $cadD3Run.Kill(); throw 'D3 face feature smoke timed out.' }
                $cadD3Result = Join-Path $cadD3Smoke 'result.txt'
                if (!(Test-Path -LiteralPath $cadD3Result)) { throw "No D3 face feature result (exit $($cadD3Run.ExitCode))." }
                Get-Content -LiteralPath $cadD3Result
                if ($cadD3Run.ExitCode -ne 0 -or !(Get-Content -LiteralPath $cadD3Result -Raw).StartsWith('PASS:')) { throw 'D3 face feature smoke failed.' }
                if ((Get-Item -LiteralPath (Join-Path $cadD3Smoke 'bindings.log')).Length -gt 0) { throw 'D3 face feature binding errors recorded.' }
                foreach ($cadLanguage in @('en-US','zh-CN','ja-JP')) {
                    foreach ($cadKind in @('shell','draft')) {
                        if (!(Test-Path -LiteralPath (Join-Path $cadD3Smoke "$cadKind-$cadLanguage.png"))) { throw "Missing D3 screenshot: $cadKind-$cadLanguage" }
                    }
                }
                Write-Output "D3 face feature evidence: $cadD3Smoke"
            }
            finally { $cadD3Run.Dispose() }
        }
        if ($DrawingSmoke) {
            $cadDrawingSmoke = Join-Path $cadRoot "artifacts\m11-drawing-smoke-$cadStamp"
            $cadStart.Arguments = '--m11-window-smoke "' + $cadDrawingSmoke + '"'
            $cadDrawingRun = [System.Diagnostics.Process]::Start($cadStart)
            try {
                if (!$cadDrawingRun.WaitForExit(60000)) { $cadDrawingRun.Kill(); throw 'Drawing window smoke timed out.' }
                $cadDrawingResult = Join-Path $cadDrawingSmoke 'result.json'
                if (!(Test-Path -LiteralPath $cadDrawingResult)) { throw "No drawing result (exit $($cadDrawingRun.ExitCode))." }
                Get-Content -LiteralPath $cadDrawingResult
                if ($cadDrawingRun.ExitCode -ne 0 -or !(Get-Content -LiteralPath $cadDrawingResult -Raw).Contains('"passed": true')) { throw 'Drawing window smoke failed.' }
                if ((Get-Item -LiteralPath (Join-Path $cadDrawingSmoke 'bindings.log')).Length -gt 0) { throw 'Drawing window binding errors recorded.' }
                foreach ($cadFile in @('drawing-window.png','drawing.pdf','drawing.cadoryx')) {
                    if (!(Test-Path -LiteralPath (Join-Path $cadDrawingSmoke $cadFile))) { throw "Missing drawing artifact: $cadFile" }
                }
                Write-Output "Drawing evidence: $cadDrawingSmoke"
            }
            finally { $cadDrawingRun.Dispose() }
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
