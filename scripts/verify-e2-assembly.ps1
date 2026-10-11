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
$cadStart.ArgumentList.Add('--e2-assembly-solve-smoke')
$cadStart.ArgumentList.Add($cadOutput)
$cadStart.Environment['CADORYX_SMOKE_BUTTON_INPUT']='automation-invoke'
$cadStart.Environment['PATH']="$env:WINDIR\System32;$env:WINDIR;$env:ProgramFiles\dotnet"
@($cadStart.Environment.Keys)|Where-Object {$_ -match '^(CSF_|CASROOT|OCCT|OCCTSHARP)'}|ForEach-Object {$cadStart.Environment.Remove($_)|Out-Null}
$cadRun=[Diagnostics.Process]::Start($cadStart)
try {
    $cadDeadline=[DateTimeOffset]::UtcNow.AddMinutes(4)
    while(!$cadRun.WaitForExit(1000)){if([DateTimeOffset]::UtcNow -gt $cadDeadline){$cadRun.Kill();throw 'E2 production assembly timed out.'}}
    $cadText=Get-Content -LiteralPath (Join-Path $cadOutput 'result.txt') -Raw
    Write-Output $cadText
    if($cadRun.ExitCode -ne 0 -or !$cadText.StartsWith('PASS:')){throw 'E2 production assembly failed.'}
    if((Get-Item -LiteralPath (Join-Path $cadOutput 'bindings.log')).Length -ne 0){throw 'E2 WPF binding errors.'}
    foreach($cadLanguage in @('en-US','zh-CN','ja-JP')){
        foreach($cadName in @('solve-preview','solve-confirm','native-preview')){
            if(!(Test-Path -LiteralPath (Join-Path $cadOutput "$cadName-$cadLanguage.png"))){throw "Missing E2 screenshot: $cadName-$cadLanguage"}
        }
        foreach($cadFile in @("solve-report-$cadLanguage.json","accepted-$cadLanguage.cadoryx")){
            if(!(Test-Path -LiteralPath (Join-Path $cadOutput $cadFile))){throw "Missing E2 evidence: $cadFile"}
        }
    }
    if(!(Test-Path -LiteralPath (Join-Path $cadOutput 'solve-conflict.png'))){throw 'Missing conflict screenshot.'}
}finally{$cadRun.Dispose()}
