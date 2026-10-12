$ErrorActionPreference='Stop'
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx'
$taskOutput=Join-Path $taskRoot 'artifacts/public85-runtime-acceptance-1'
$taskMatches=@(Get-CimInstance Win32_Process -Filter "Name='pwsh.exe'"|Where-Object {$_.CommandLine -match ' -NoProfile -File artifacts/run-public85-native-full-both-1\.ps1$'})
if($taskMatches.Count -ne 1){throw 'Exactly one observed public85 runtime coordinator required'}
$taskRunner=Get-Process -Id $taskMatches[0].ProcessId
try{
 while(!(Test-Path -LiteralPath (Join-Path $taskOutput 'full-both-exit.json'))){
  if($taskRunner.HasExited){throw 'Public85 coordinator exited without both terminal; preserve original evidence'}
  $null=$taskRunner.WaitForExit(5000)
 }
 & 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' (Join-Path $taskRoot 'artifacts/audit-public85-native-full-both-1.py')
 if($LASTEXITCODE){throw 'Independent public85 full runtime audit failed; inspect original evidence'}
}catch{
 $taskFailure=Join-Path $taskOutput 'audit-watcher-failure.json'
 if(!(Test-Path -LiteralPath $taskFailure)){@{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');error=$_.ToString();AF='ACTIVE';runtimeAccepted=$false}|ConvertTo-Json|Set-Content -LiteralPath $taskFailure -Encoding utf8}
 throw
}finally{$taskRunner.Dispose()}
