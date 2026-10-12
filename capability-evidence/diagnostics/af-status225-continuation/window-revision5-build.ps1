$ErrorActionPreference='Stop'
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx';$taskBuild=Join-Path $taskRoot 'artifacts/d6-window-host-5'
$taskPreparation=Get-Content -LiteralPath (Join-Path $taskBuild 'preparation-1.json') -Raw|ConvertFrom-Json
foreach($taskRow in $taskPreparation.source){if((Get-FileHash -LiteralPath $taskRow.path).Hash -cne $taskRow.sha256){throw 'Frozen observer source drift'}}
. (Join-Path $taskRoot 'artifacts/wait-cad-native-consumers-idle-1.ps1')
$null=Wait-CadNativeConsumersIdle
$taskStarted=[DateTimeOffset]::UtcNow;$taskResults=@()
foreach($taskConfiguration in @('Release','Debug')) {
 $taskLabel=$taskConfiguration.ToLower();$taskTerminal=Join-Path $taskBuild ('build-'+$taskLabel+'-1-exit.json')
 if(Test-Path -LiteralPath $taskTerminal){throw 'Immutable observer build exists'}
 dotnet build (Join-Path $taskBuild 'WindowHost.csproj') -c $taskConfiguration ('-p:CandidatePayload='+$taskRoot+'/artifacts/d6-finite-newbody-draft-4/workspace-1/Cadoryx.wpf/bin/'+$taskConfiguration+'/net10.0-windows/win-x64') --nologo -v minimal > (Join-Path $taskBuild ('build-'+$taskLabel+'-1.log')) 2> (Join-Path $taskBuild ('build-'+$taskLabel+'-1-stderr.log'))
 $taskCode=$LASTEXITCODE
 @{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');exitCode=$taskCode;actualWpfWindowsStarted=$false;cleanup=$false}|ConvertTo-Json|Set-Content -LiteralPath $taskTerminal -Encoding utf8
 $taskResults+=@{configuration=$taskConfiguration;exitCode=$taskCode;terminal=$taskTerminal;sha256=(Get-FileHash -LiteralPath $taskTerminal).Hash}
 if($taskCode -ne 0){throw 'Observer compile failed; original log preserved'}
}
@{started=$taskStarted.ToString('o');completed=[DateTimeOffset]::UtcNow.ToString('o');gates=$taskResults;actualWpfWindowsStarted=$false;cleanup=$false;AF='ACTIVE';G='USER_DEFERRED'}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $taskBuild 'build-both-1-exit.json') -Encoding utf8
& 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' -X utf8 (Join-Path $taskBuild 'build-audit.py')
if($LASTEXITCODE -ne 0){throw 'Observer build audit failed; build results preserved'}
