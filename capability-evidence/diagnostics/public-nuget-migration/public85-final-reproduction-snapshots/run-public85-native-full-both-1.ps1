$ErrorActionPreference='Stop'
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx'
$taskOutput=Join-Path $taskRoot 'artifacts/public85-runtime-acceptance-1'
$taskTerminal=Join-Path $taskOutput 'full-both-exit.json'
if(Test-Path -LiteralPath $taskTerminal){throw 'Immutable full suite terminal exists'}
$taskRows=@();$taskStarted=[DateTimeOffset]::UtcNow
foreach($taskConfiguration in @('Release','Debug')){
 & 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/native/powershell/pwsh.exe' -NoProfile -File (Join-Path $taskRoot 'artifacts/run-public85-native-gate-1.ps1') -Configuration $taskConfiguration -Group full6619
 $taskCode=$LASTEXITCODE
 $taskPath=Join-Path $taskOutput ('full6619-'+$taskConfiguration.ToLower()+'-1/exit.json')
 if(!(Test-Path -LiteralPath $taskPath)){throw 'No actual native runtime terminal; inspect before continuing'}
 $taskActual=Get-Content -LiteralPath $taskPath -Raw|ConvertFrom-Json
 if($taskActual.exitCode -ne $taskCode){throw 'Process and runtime terminal disagree'}
 $taskRows+=@{configuration=$taskConfiguration;exitCode=$taskCode;terminal=$taskPath;sha256=(Get-FileHash -LiteralPath $taskPath).Hash}
}
@{started=$taskStarted.ToString('o');completed=[DateTimeOffset]::UtcNow.ToString('o');version='8.0.1-preview.85';gates=$taskRows;source1073Applied=$false;desktopInputStarted=$false;cleanup=$false;AF='ACTIVE';G='USER_DEFERRED'}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $taskTerminal -Encoding utf8
