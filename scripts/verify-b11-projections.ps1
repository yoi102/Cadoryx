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
$cadStart.ArgumentList.Add('--b11-native-projection-smoke')
$cadStart.ArgumentList.Add($cadOutput)
$cadStart.Environment['CADORYX_SMOKE_BUTTON_INPUT']='automation-invoke'
$cadStart.Environment['PATH']="$env:WINDIR\System32;$env:WINDIR;$env:ProgramFiles\dotnet"
@($cadStart.Environment.Keys)|Where-Object {$_ -match '^(CSF_|CASROOT|OCCT|OCCTSHARP)'}|ForEach-Object {$cadStart.Environment.Remove($_)|Out-Null}
$cadRun=[Diagnostics.Process]::Start($cadStart)
try {
    $cadDeadline=[DateTimeOffset]::UtcNow.AddMinutes(6)
    while(!$cadRun.WaitForExit(1000)){if([DateTimeOffset]::UtcNow -gt $cadDeadline){$cadRun.Kill();throw 'B11 production native projection timed out.'}}
    $cadText=Get-Content -LiteralPath (Join-Path $cadOutput 'result.txt') -Raw
    Write-Output $cadText
    if($cadRun.ExitCode -ne 0 -or !$cadText.StartsWith('PASS:')){throw 'B11 production native projection failed.'}
    if((Get-Item -LiteralPath (Join-Path $cadOutput 'bindings.log')).Length -ne 0){throw 'B11 WPF binding errors.'}
    foreach($cadLanguage in @('en-US','zh-CN','ja-JP')){
        foreach($cadName in @('picker','edge','reject','conic','native')){
            if(!(Test-Path -LiteralPath (Join-Path $cadOutput "$cadName-$cadLanguage.png"))){throw "Missing B11 screenshot: $cadName-$cadLanguage"}
        }
        if(!(Test-Path -LiteralPath (Join-Path $cadOutput "accepted-$cadLanguage.cadoryx"))){throw "Missing B11 saved document: $cadLanguage"}
        foreach($cadKind in 0..2){
            foreach($cadName in @('freeform-edge','freeform-editor')){
                if(!(Test-Path -LiteralPath (Join-Path $cadOutput "$cadName-$cadKind-$cadLanguage.png"))){throw "Missing B11 freeform screenshot: $cadName-$cadKind-$cadLanguage"}
            }
            if(!(Test-Path -LiteralPath (Join-Path $cadOutput "freeform-$cadKind-$cadLanguage.cadoryx"))){throw "Missing B11 freeform saved document: $cadKind-$cadLanguage"}
        }
    }
}finally{$cadRun.Dispose()}
