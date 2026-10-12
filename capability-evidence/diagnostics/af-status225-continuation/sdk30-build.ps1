$ErrorActionPreference='Stop'
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx'
$taskDraft=Join-Path $taskRoot 'artifacts/d6-sdk30-production-integration-1'
$taskProof=Get-Content -LiteralPath (Join-Path $taskDraft 'preparation-1.json') -Raw|ConvertFrom-Json
$taskExit=Join-Path $taskDraft 'build-both-1-exit.json'
if(Test-Path -LiteralPath $taskExit){throw 'Immutable SDK30 integration build exists'}
. (Join-Path $taskRoot 'artifacts/wait-cad-native-consumers-idle-1.ps1')
$null=Wait-CadNativeConsumersIdle
foreach($taskRow in $taskProof.sourcePayload){if((Get-FileHash -LiteralPath (Join-Path $taskProof.workspace $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Frozen D6 SDK30 source1066 drift'}}
$taskOriginal=Get-Content -LiteralPath $taskProof.baselinePreparation.path -Raw|ConvertFrom-Json
$taskStarted=[DateTimeOffset]::UtcNow;$taskResults=@()
Push-Location -LiteralPath $taskProof.workspace
try{
 $env:NUGET_PACKAGES=$taskOriginal.privateCache
 foreach($taskStep in @('restore-locked','build-release','inventory-release','build-debug','inventory-debug')){
  $taskTerminal=Join-Path $taskDraft ($taskStep+'-1-exit.json');if(Test-Path -LiteralPath $taskTerminal){throw 'Immutable integration stage exists'}
  $taskLog=Join-Path $taskDraft ($taskStep+'-1.log');$taskError=Join-Path $taskDraft ($taskStep+'-1-stderr.log')
  switch($taskStep){
   'restore-locked'{dotnet restore Cadoryx.slnx --locked-mode --nologo -v minimal > $taskLog 2> $taskError}
   'build-release'{dotnet build Cadoryx.slnx -c Release --no-restore --nologo -v minimal > $taskLog 2> $taskError}
   'build-debug'{dotnet build Cadoryx.slnx -c Debug --no-restore --nologo -v minimal > $taskLog 2> $taskError}
   'inventory-release'{dotnet test Cadoryx.Tests/Cadoryx.Tests.csproj -c Release --no-build --no-restore --list-tests --nologo > $taskLog 2> $taskError}
   'inventory-debug'{dotnet test Cadoryx.Tests/Cadoryx.Tests.csproj -c Debug --no-build --no-restore --list-tests --nologo > $taskLog 2> $taskError}
  }
  $taskCode=$LASTEXITCODE
  @{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');step=$taskStep;exitCode=$taskCode;actualRuntimeTestsStarted=$false;actualWpfWindowsStarted=$false;cleanup=$false}|ConvertTo-Json|Set-Content -LiteralPath $taskTerminal -Encoding utf8
  $taskResults+=@{step=$taskStep;exitCode=$taskCode;terminal=$taskTerminal;sha256=(Get-FileHash -LiteralPath $taskTerminal).Hash}
  if($taskCode -ne 0){throw 'SDK30 integration stage failed; original output preserved'}
 }
 @{started=$taskStarted.ToString('o');completed=[DateTimeOffset]::UtcNow.ToString('o');gates=$taskResults;actualRuntimeTestsStarted=$false;actualWpfWindowsStarted=$false;cleanup=$false;AF='ACTIVE';G='USER_DEFERRED'}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $taskExit -Encoding utf8
}finally{Pop-Location}
