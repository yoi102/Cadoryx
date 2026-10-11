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
$cadStart.ArgumentList.Add('--b8-revolve-smoke')
$cadStart.ArgumentList.Add($cadOutput)
$cadStart.Environment['CADORYX_SMOKE_BUTTON_INPUT']='automation-invoke'
$cadStart.Environment['PATH']="$env:WINDIR\System32;$env:WINDIR;$env:ProgramFiles\dotnet"
@($cadStart.Environment.Keys)|Where-Object {$_ -match '^(CSF_|CASROOT|OCCT|OCCTSHARP)'}|ForEach-Object {$cadStart.Environment.Remove($_)|Out-Null}
$cadRun=[Diagnostics.Process]::Start($cadStart)
try {
    if(!$cadRun.WaitForExit(240000)){$cadRun.Kill();throw 'B8 production revolve smoke timed out.'}
    $cadText=Get-Content -LiteralPath (Join-Path $cadOutput 'result.txt') -Raw
    Write-Output $cadText
    if($cadRun.ExitCode -ne 0 -or !$cadText.StartsWith('PASS:')){throw 'B8 production revolve smoke failed.'}
    if((Get-Item -LiteralPath (Join-Path $cadOutput 'bindings.log')).Length -ne 0){throw 'B8 WPF binding errors.'}
    foreach($cadLanguage in @('en-US','zh-CN','ja-JP')){foreach($cadKind in @('bezier','spline','regions')){
        foreach($cadFile in @("revolve-$cadKind-$cadLanguage.png","native-$cadKind-$cadLanguage.png","accepted-$cadKind-$cadLanguage.cadoryx")){
            if(!(Test-Path -LiteralPath (Join-Path $cadOutput $cadFile))){throw "Missing B8 evidence: $cadFile"}
        }
    }}
}finally{$cadRun.Dispose()}
