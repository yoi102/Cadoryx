$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskStage=Join-Path $taskRoot 'artifacts/af-final87-1'
. (Join-Path $taskRoot 'tools/CurrentCadCandidate/require-current-return.ps1')
Assert-CurrentSdkNativeReturn -TaskRoot $taskRoot
. (Join-Path $taskRoot 'tools/CurrentCadCandidate/deferred-step-filter.ps1')
. (Join-Path $taskRoot 'artifacts/assert-cad-native-consumers-idle-2.ps1')
. (Join-Path $taskRoot 'artifacts/a5-native-binding-helper-1.ps1')
$taskFreeze=Get-Content (Join-Path $taskStage 'freeze.json') -Raw|ConvertFrom-Json -DateKind String
if($taskFreeze.version -cne '8.0.1-preview.87' -or $taskFreeze.expectedTotal -ne 7192 -or $taskFreeze.expectedExecuted -ne 7168){throw 'Current final87 exact freeze required'}
if(Test-Path (Join-Path $taskStage 'full-both-exit.json')){throw 'Preserve immutable runtime result'}
$taskRows=@()
foreach($taskConfig in $taskFreeze.configurations){
 $taskUnrelated=@(Assert-CadNativeConsumersIdle)
 foreach($taskSource in $taskFreeze.candidateSource){if((Get-FileHash (Join-Path $taskFreeze.workspace $taskSource.path)).Hash -cne $taskSource.sha256){throw 'Frozen application input changed'}}
 foreach($taskFile in $taskConfig.payload){if((Get-FileHash (Join-Path $taskConfig.testDirectory $taskFile.path)).Hash -cne $taskFile.sha256){throw 'Frozen test payload changed'}}
 $taskFilter=New-CadDeferredStepFilter -TaskRoot $taskRoot -DiscoveredNames $taskConfig.discoveredNames
 $taskOutput=Join-Path $taskStage ('full-'+$taskConfig.configuration.ToLower()+'-1')
 if(Test-Path $taskOutput){throw 'Preserve immutable config run'}
 $null=New-Item -ItemType Directory -Path $taskOutput
 $taskInfo=[Diagnostics.ProcessStartInfo]::new((Get-Command dotnet).Source)
 $taskInfo.WorkingDirectory=$taskFreeze.workspace;$taskInfo.UseShellExecute=$false;$taskInfo.CreateNoWindow=$true;$taskInfo.WindowStyle='Hidden'
 $taskInfo.RedirectStandardOutput=$true;$taskInfo.RedirectStandardError=$true
 foreach($taskArg in @('vstest',(Join-Path $taskConfig.testDirectory 'Cadoryx.Tests.dll'),('/TestCaseFilter:'+$taskFilter),'/Logger:console;verbosity=normal','/Logger:trx;LogFileName=runtime.trx',('/ResultsDirectory:'+$taskOutput))){$taskInfo.ArgumentList.Add($taskArg)}
 $taskProcess=[Diagnostics.Process]::Start($taskInfo);$taskStarted=[DateTimeOffset]::UtcNow
 @{started=$taskStarted.ToString('o');pid=$taskProcess.Id;coordinatorPid=$PID;configuration=$taskConfig.configuration;testDirectory=$taskConfig.testDirectory;expectedExecuted=7168;deferred24NotRun=$true;filter=$taskFilter;unrelatedPreserved=$taskUnrelated}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $taskOutput 'start.json')
 $taskOut=[IO.File]::Open((Join-Path $taskOutput 'stdout.log'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
 $taskErr=[IO.File]::Open((Join-Path $taskOutput 'stderr.log'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
 $taskCopy=$taskProcess.StandardOutput.BaseStream.CopyToAsync($taskOut);$taskErrorCopy=$taskProcess.StandardError.BaseStream.CopyToAsync($taskErr)
 $taskBinding=Join-Path $taskOutput 'loaded-native.json'
 Write-Output ('Current final87 '+$taskConfig.configuration+'7168 started PID='+$taskProcess.Id+'; exact24 deferred, no desktop input')
 try{
  while(!$taskProcess.WaitForExit(100)){
   if(!(Test-Path $taskBinding)){
    foreach($taskHost in @(Get-Process testhost -ErrorAction SilentlyContinue)){
     if($taskHost.Path -ceq (Join-Path $taskConfig.testDirectory 'testhost.exe')){$null=Save-A5NativeBinding -ProcessId $taskHost.Id -NativeDirectory (Join-Path $taskConfig.testDirectory 'occt') -Expected $taskFreeze.native62 -OutputPath $taskBinding}
    }
   }
  }
  $taskCode=$taskProcess.ExitCode
 }finally{$null=$taskCopy.GetAwaiter().GetResult();$null=$taskErrorCopy.GetAwaiter().GetResult();$taskOut.Dispose();$taskErr.Dispose();$taskProcess.Dispose()}
 $taskRow=@{configuration=$taskConfig.configuration;exitCode=$taskCode;completed=[DateTimeOffset]::UtcNow.ToString('o');output=$taskOutput;actualNativeBinding=(Test-Path $taskBinding);STEP24Retried=$false}
 $taskRow|ConvertTo-Json|Set-Content (Join-Path $taskOutput 'exit.json');$taskRows+=@($taskRow)
 Get-Content (Join-Path $taskOutput 'stdout.log') -Tail 4
 if(!(Test-Path $taskBinding)){throw 'Actual62 binding not captured'}
 Assert-CadDeferredStepResults -TaskRoot $taskRoot -DiscoveredNames $taskConfig.discoveredNames -TrxPath (Join-Path $taskOutput 'runtime.trx') -ExitCode $taskCode
}
@{configurations=$taskRows;totalDiscovered=7192;executedEach=7168;deferred24NotRun=$true;productionApplied=$false;AFComplete=$false}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $taskStage 'full-both-exit.json')
