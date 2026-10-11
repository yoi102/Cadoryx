param([Parameter(Mandatory=$true)][string]$Publish,[Parameter(Mandatory=$true)][string]$OutputDirectory,
      [Parameter(Mandatory=$true)][ValidateSet('Miter','Round','Bevel')][string]$Join)
$ErrorActionPreference='Stop'
$cadPublish=[IO.Path]::GetFullPath($Publish)
$cadOutput=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $cadOutput){throw "Evidence already exists: $cadOutput"}
if(Get-Process -Name testhost,AnalyticalOffsetNativeProbe -ErrorAction SilentlyContinue){throw 'Native consumers, WPF and tests must run serially.'}
$cadStart=[Diagnostics.ProcessStartInfo]::new()
$cadStart.FileName=Join-Path $cadPublish 'Cadoryx.wpf.exe'
$cadStart.WorkingDirectory=$cadPublish
$cadStart.UseShellExecute=$false
$cadStart.CreateNoWindow=$true
$cadStart.WindowStyle='Hidden'
$cadStart.ArgumentList.Add('--b17-'+$Join.ToLowerInvariant()+'-smoke')
$cadStart.ArgumentList.Add($cadOutput)
$cadStart.Environment['CADORYX_SMOKE_BUTTON_INPUT']='automation-invoke'
$cadStart.Environment['PATH']="$env:WINDIR\System32;$env:WINDIR;$env:ProgramFiles\dotnet"
@($cadStart.Environment.Keys)|Where-Object {$_ -match '^(CSF_|CASROOT|OCCT|OCCTSHARP)'}|ForEach-Object {$cadStart.Environment.Remove($_)|Out-Null}
$cadRun=[Diagnostics.Process]::Start($cadStart)
try {
    $cadDeadline=[DateTimeOffset]::UtcNow.AddMinutes(4)
    while(!$cadRun.WaitForExit(1000)){if([DateTimeOffset]::UtcNow -gt $cadDeadline){$cadRun.Kill();throw 'B17 production offset timed out.'}}
    $cadText=Get-Content -LiteralPath (Join-Path $cadOutput 'result.txt') -Raw
    Write-Output $cadText
    if($cadRun.ExitCode -ne 0 -or !$cadText.StartsWith('PASS:')){throw 'B17 production offset failed.'}
    if((Get-Item -LiteralPath (Join-Path $cadOutput 'bindings.log')).Length -ne 0){throw 'B17 WPF binding errors.'}
    foreach($cadLanguage in @('en-US','zh-CN','ja-JP')){
        foreach($cadName in @('reject','offset','native','loop')){
            if(!(Test-Path -LiteralPath (Join-Path $cadOutput "$cadName-$cadLanguage.png"))){throw "Missing B17 screenshot: $cadName-$cadLanguage"}
        }
        if(!(Test-Path -LiteralPath (Join-Path $cadOutput "accepted-$cadLanguage.cadoryx"))){throw "Missing B17 saved document: $cadLanguage"}
    }
}finally{$cadRun.Dispose()}
