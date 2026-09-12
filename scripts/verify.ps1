param([switch]$PublishSmoke)
$ErrorActionPreference = 'Stop'
$cadRoot = Split-Path -Parent $PSScriptRoot
Push-Location $cadRoot
try {
    dotnet restore Cadoryx.slnx --locked-mode --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    dotnet build Cadoryx.slnx -c Release --no-restore --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    dotnet test Cadoryx.Tests -c Release --no-build --no-restore --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    if ($PublishSmoke) {
        $cadStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $cadPublish = Join-Path $cadRoot "artifacts\publish\$cadStamp"
        $cadSmoke = Join-Path $cadRoot "artifacts\smoke-$cadStamp"
        dotnet publish Cadoryx.wpf/Cadoryx.wpf.csproj -c Release --self-contained false --no-restore -o $cadPublish --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        foreach ($cadFile in @('occt\OcctSharp.Native.dll','runtime-baseline.json','licenses\OcctSharp.Native\THIRD_PARTY_NOTICES.md')) {
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
            if (!$cadRun.WaitForExit(30000)) { $cadRun.Kill(); throw 'Desktop smoke timed out.' }
            $cadResult = Join-Path $cadSmoke 'result.txt'
            if (!(Test-Path -LiteralPath $cadResult)) { throw "No smoke result (exit $($cadRun.ExitCode))." }
            Get-Content -LiteralPath $cadResult
            if ($cadRun.ExitCode -ne 0 -or !(Get-Content -LiteralPath $cadResult -Raw).StartsWith('PASS:')) { throw 'Desktop smoke failed.' }
            if ((Get-Item -LiteralPath (Join-Path $cadSmoke 'bindings.log')).Length -gt 0) { throw 'WPF binding errors recorded.' }
            Write-Output "Verified publish: $cadPublish"
            Write-Output "Evidence: $cadSmoke"
        }
        finally { $cadRun.Dispose() }
    }
}
finally { Pop-Location }
