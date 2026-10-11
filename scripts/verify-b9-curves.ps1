param([Parameter(Mandatory=$true)][string]$Publish,[Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$cadPublish=[IO.Path]::GetFullPath($Publish)
$cadOutput=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $cadOutput){throw "Evidence already exists: $cadOutput"}
$cadStart=[Diagnostics.ProcessStartInfo]::new()
$cadStart.FileName=Join-Path $cadPublish 'Cadoryx.wpf.exe'
$cadStart.WorkingDirectory=$cadPublish
$cadStart.UseShellExecute=$false
$cadStart.CreateNoWindow=$true
$cadStart.WindowStyle='Hidden'
$cadStart.ArgumentList.Add('--b9-curve-editor-smoke')
$cadStart.ArgumentList.Add($cadOutput)
$cadStart.Environment['CADORYX_SMOKE_BUTTON_INPUT']='automation-invoke'
$cadStart.Environment['PATH']="$env:WINDIR\System32;$env:WINDIR;$env:ProgramFiles\dotnet"
@($cadStart.Environment.Keys)|Where-Object {$_ -match '^(CSF_|CASROOT|OCCT|OCCTSHARP)'}|ForEach-Object {$cadStart.Environment.Remove($_)|Out-Null}
$cadRun=[Diagnostics.Process]::Start($cadStart)
try {
    $cadDeadline=[DateTimeOffset]::UtcNow.AddMinutes(5)
    while(!$cadRun.WaitForExit(1000)){if([DateTimeOffset]::UtcNow -gt $cadDeadline){$cadRun.Kill();throw 'B9 production curve editor timed out.'}}
    $cadText=Get-Content -LiteralPath (Join-Path $cadOutput 'result.txt') -Raw
    Write-Output $cadText
    if($cadRun.ExitCode -ne 0 -or !$cadText.StartsWith('PASS:')){throw 'B9 production curve editor failed.'}
    if((Get-Item -LiteralPath (Join-Path $cadOutput 'bindings.log')).Length -ne 0){throw 'B9 WPF binding errors.'}
    foreach($cadLanguage in @('en-US','zh-CN','ja-JP')){
        if(!(Test-Path -LiteralPath (Join-Path $cadOutput "knots-$cadLanguage.png"))){throw "Missing knot screenshot: $cadLanguage"}
        foreach($cadKind in @('bezier','spline')){
            foreach($cadFile in @("interval-$cadKind-$cadLanguage.png","native-$cadKind-$cadLanguage.png","split-$cadKind-$cadLanguage.png","reselect-$cadKind-$cadLanguage.png","accepted-$cadKind-$cadLanguage.cadoryx","accepted-split-$cadKind-$cadLanguage.cadoryx")){
                if(!(Test-Path -LiteralPath (Join-Path $cadOutput $cadFile))){throw "Missing B9 evidence: $cadFile"}
            }
        }
    }
}finally{$cadRun.Dispose()}
