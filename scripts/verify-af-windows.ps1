param([Parameter(Mandatory=$true)][string]$Publish,[Parameter(Mandatory=$true)][string]$EvidencePrefix,[string[]]$Only=@())
$ErrorActionPreference='Stop'
$cadRoot=Split-Path -Parent $PSScriptRoot
$cadPublish=[IO.Path]::GetFullPath($Publish)
$cadPrefix=[IO.Path]::GetFullPath($EvidencePrefix)
$cadDefinitions=[ordered]@{
    'b11-endpoints'='--b11-endpoint-smoke'; 'b11-projections'='--b11-native-projection-smoke'
    'b11-parameters'='--b11-parameter-smoke'
    'b11-periodic'='--b11-periodic-smoke'
    'b12-contact'='--b12-contact-smoke'
    'b13-curvature'='--b13-curvature-smoke'
    'b14-finite-contact'='--b14-finite-contact-smoke'
    'b15-mixed-continuity'='--b15-mixed-continuity-smoke'
    'b16-solve-a'='--b16-sliding-a-smoke'; 'b16-solve-b'='--b16-sliding-b-smoke'; 'b16-solve-both'='--b16-sliding-both-smoke'
    'e2-assembly'='--e2-assembly-solve-smoke'; 'e1-history'='--e1-history-smoke'
    'b9-curves'='--b9-curve-editor-smoke'; 'b8-revolve'='--b8-revolve-smoke'
    'desktop'='--smoke'; 'window'='--window-smoke'
    'c1-parameter'='--c1-parameter-smoke'; 'c2-datum'='--c2-datum-smoke'
    'c3-repetition'='--c3-repetition-smoke'; 'c3-assembly'='--c3-assembly-smoke'
    'c4-hole'='--c4-hole-smoke'; 'd1-extrusion'='--d1-extrusion-smoke'
    'c5-thread'='--c5-thread-smoke'
    'd2-guided'='--d2-guided-smoke'; 'd3-face-feature'='--d3-face-feature-smoke'
    'd4-solid-offset'='--d4-solid-offset-smoke'
    'd5-surface-extrusion'='--d5-surface-extrusion-smoke'
    'e3-contour'='--e3-contour-smoke'
    'e4-face-edit'='--e4-face-edit-smoke'
    'drawing'='--m11-window-smoke'
    'a4-cancel'='--a4-drawing-cancel-smoke'
    'f3e-datums'='--f3e-typed-datum-smoke'; 'f3c-holes'='--f3c-hole-table-smoke'
    'f3f-history'='--f3f-attachment-history-smoke'
    'f3g-composed'='--f3g-composed-drilling-smoke'
    'f3h-planar'='--f3h-planar-size-smoke'
}
foreach($cadRequested in $Only){if(!$cadDefinitions.Contains($cadRequested)){throw "Unknown gate: $cadRequested"}}
$cadGates=if($Only.Count){$Only}else{@($cadDefinitions.Keys)}
if(Test-Path -LiteralPath ($cadPrefix+'-summary.json')){throw 'Evidence summary already exists.'}
foreach($cadGate in $cadGates){if(Test-Path -LiteralPath ($cadPrefix+'-'+$cadGate)){throw "Evidence already exists: $cadGate"}}
$cadSummary=[ordered]@{publish=$cadPublish;started=[DateTimeOffset]::Now.ToString('O');input='WPF Automation; physical input separate';binaryHashes=@();gates=@()}
$cadSummary.binaryHashes=@(Get-ChildItem -LiteralPath $cadPublish -File|Where-Object {$_.Name -match '^Cadoryx\..*\.(dll|exe)$'}|ForEach-Object {@{path=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
foreach($cadGate in $cadGates){
    $cadOutput=$cadPrefix+'-'+$cadGate
    $cadEntry=[ordered]@{name=$cadGate;output=$cadOutput;status='FAIL';started=[DateTimeOffset]::Now.ToString('O');exitCode=$null;error=$null}
    $cadStart=[Diagnostics.ProcessStartInfo]::new()
    $cadStart.FileName=Join-Path $cadPublish 'Cadoryx.wpf.exe';$cadStart.WorkingDirectory=$cadPublish
    $cadStart.UseShellExecute=$false;$cadStart.CreateNoWindow=$true;$cadStart.WindowStyle='Hidden'
    $cadStart.RedirectStandardOutput=$true;$cadStart.RedirectStandardError=$true
    $cadStart.ArgumentList.Add($cadDefinitions[$cadGate]);$cadStart.ArgumentList.Add($cadOutput)
    if($cadGate -eq 'window'){$cadStart.ArgumentList.Add((Join-Path $cadRoot 'Cadoryx.Tests/Fixtures/Exchange'))}
    if($cadGate -eq 'a4-cancel'){$cadStart.ArgumentList.Add((Join-Path $cadRoot 'Cadoryx.Tests/Fixtures/Drawing/modeled-m6-left-blind.brep'))}
    $cadStart.Environment['CADORYX_SMOKE_BUTTON_INPUT']='automation-invoke'
    $cadStart.Environment['PATH']="$env:WINDIR\System32;$env:WINDIR;$env:ProgramFiles\dotnet"
    @($cadStart.Environment.Keys)|Where-Object {$_ -match '^(CSF_|CASROOT|OCCT|OCCTSHARP)'}|ForEach-Object {$cadStart.Environment.Remove($_)|Out-Null}
    $cadRun=$null;$cadStdout=$null;$cadStderr=$null
    try{
        $cadRun=[Diagnostics.Process]::Start($cadStart)
        $cadStdout=$cadRun.StandardOutput.ReadToEndAsync();$cadStderr=$cadRun.StandardError.ReadToEndAsync()
        Write-Output "$cadGate started: $($cadRun.Id)"
        $cadMinutes=if($cadGate -eq 'f3g-composed'){10}else{5}
        $cadEntry.timeoutMinutes=$cadMinutes
        $cadDeadline=[DateTimeOffset]::UtcNow.AddMinutes($cadMinutes)
        while(!$cadRun.WaitForExit(1000)){if([DateTimeOffset]::UtcNow -gt $cadDeadline){$cadRun.Kill();throw "$cadGate timed out"}}
        $cadEntry.exitCode=$cadRun.ExitCode
        $cadJsonGate=$cadGate -in @('drawing','a4-cancel','f3e-datums','f3c-holes','f3f-history','f3g-composed','f3h-planar')
        $cadResult=Join-Path $cadOutput $(if($cadJsonGate){'result.json'}else{'result.txt'})
        $cadText=Get-Content -LiteralPath $cadResult -Raw
        $cadPassed=if($cadJsonGate){($cadText|ConvertFrom-Json).passed}else{$cadText.StartsWith('PASS:')}
        if($cadRun.ExitCode -ne 0 -or !$cadPassed){throw $cadText}
        if((Get-Item -LiteralPath (Join-Path $cadOutput 'bindings.log')).Length -ne 0){throw 'WPF binding errors'}
        $cadEntry.status='PASS';Write-Output "$cadGate PASS"
    }catch{$cadEntry.error=$_.Exception.Message;Write-Output "$cadGate FAIL: $($cadEntry.error)"}
    finally{
        if($null -ne $cadRun){
            if($cadRun.HasExited){
                $cadEntry.exitCode=$cadRun.ExitCode
                [IO.Directory]::CreateDirectory($cadOutput)|Out-Null
                [IO.File]::WriteAllText((Join-Path $cadOutput 'stdout.log'),$cadStdout.GetAwaiter().GetResult())
                [IO.File]::WriteAllText((Join-Path $cadOutput 'stderr.log'),$cadStderr.GetAwaiter().GetResult())
            }
            $cadRun.Dispose()
        }
    }
    $cadEntry.completed=[DateTimeOffset]::Now.ToString('O');$cadSummary.gates+=@($cadEntry)
    [IO.File]::WriteAllText(($cadPrefix+'-summary.json'),($cadSummary|ConvertTo-Json -Depth 8))
}
$cadSummary.completed=[DateTimeOffset]::Now.ToString('O')
[IO.File]::WriteAllText(($cadPrefix+'-summary.json'),($cadSummary|ConvertTo-Json -Depth 8))
if(@($cadSummary.gates|Where-Object {$_.status -ne 'PASS'}).Count){throw 'Some A-F window gates failed; all requested gates have independent results in the summary.'}
