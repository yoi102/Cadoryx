$ErrorActionPreference='Stop'
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx'
$taskDraft=Join-Path $taskRoot 'artifacts/d7-analytic-branch-draft-1'
$taskFreeze=Get-Content (Join-Path $taskDraft 'freeze.json') -Raw|ConvertFrom-Json -DateKind String
$taskSuite=Join-Path $taskDraft 'focused-both-exit.json'
if(Test-Path $taskSuite){throw 'Preserve the completed candidate suite'}
. (Join-Path $taskRoot 'artifacts/assert-cad-native-consumers-idle-2.ps1')
. (Join-Path $taskRoot 'artifacts/a5-native-binding-helper-1.ps1')
foreach($taskRow in $taskFreeze.toolSource){if((Get-FileHash (Join-Path $taskRoot $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Prepared candidate source changed'}}
$taskRows=@();$taskStarted=[DateTimeOffset]::UtcNow
foreach($taskMode in @('baseline','candidate')){
 foreach($taskConfig in $taskFreeze.configurations){
  $taskIdle=@(Assert-CadNativeConsumersIdle)
  $taskBinary=if($taskMode -eq 'baseline'){$taskConfig.baselineDirectory}else{$taskConfig.testDirectory}
  $taskPayload=if($taskMode -eq 'baseline'){$taskConfig.baselinePayload}else{$taskConfig.payload}
  foreach($taskRow in $taskPayload){if((Get-FileHash (Join-Path $taskBinary $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Frozen test payload differs'}}
  $taskFilter=if($taskMode -eq 'baseline'){$taskFreeze.baselineFilter}else{$taskFreeze.focusedFilter}
  $taskOutput=Join-Path $taskDraft ($taskMode+'-'+$taskConfig.configuration.ToLower()+'-runtime-1')
  if(Test-Path $taskOutput){throw 'Preserve prior runtime evidence'}
  New-Item -ItemType Directory -Path $taskOutput|Out-Null
  $taskInfo=[Diagnostics.ProcessStartInfo]::new((Get-Command dotnet).Source)
  $taskInfo.WorkingDirectory=$taskRoot;$taskInfo.UseShellExecute=$false;$taskInfo.CreateNoWindow=$true;$taskInfo.WindowStyle='Hidden'
  $taskInfo.RedirectStandardOutput=$true;$taskInfo.RedirectStandardError=$true
  foreach($taskArg in @('vstest',(Join-Path $taskBinary 'Cadoryx.Tests.dll'),('/TestCaseFilter:'+$taskFilter),'/Logger:console;verbosity=normal','/Logger:trx;LogFileName=runtime.trx',('/ResultsDirectory:'+$taskOutput))){$taskInfo.ArgumentList.Add($taskArg)}
  $taskBegin=[DateTimeOffset]::UtcNow;$taskProcess=[Diagnostics.Process]::Start($taskInfo)
  $taskProcess.Refresh()
  @{recordedAt=$taskBegin.ToString('o');pid=$taskProcess.Id;processStarted=$taskProcess.StartTime.ToUniversalTime().ToString('o');coordinatorPid=$PID;mode=$taskMode;configuration=$taskConfig.configuration;testDirectory=$taskBinary;filter=$taskFilter;unrelatedProcessesPreserved=$taskIdle}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $taskOutput 'start.json')
  Write-Output ($taskMode+' '+$taskConfig.configuration+' actual tests started PID='+$taskProcess.Id)
  $taskLog=[IO.File]::Open((Join-Path $taskOutput 'stdout.log'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
  $taskErr=[IO.File]::Open((Join-Path $taskOutput 'stderr.log'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
  $taskCopy=$taskProcess.StandardOutput.BaseStream.CopyToAsync($taskLog);$taskErrorCopy=$taskProcess.StandardError.BaseStream.CopyToAsync($taskErr)
  $taskBinding=Join-Path $taskOutput 'loaded-native.json'
  try{
   while(!$taskProcess.WaitForExit(100)){
    if(!(Test-Path $taskBinding)){
     foreach($taskHost in @(Get-Process testhost -ErrorAction SilentlyContinue)){
      if($taskHost.Path -ceq (Join-Path $taskBinary 'testhost.exe')){$null=Save-A5NativeBinding -ProcessId $taskHost.Id -NativeDirectory (Join-Path $taskBinary 'occt') -Expected $taskFreeze.native62 -OutputPath $taskBinding}
     }
    }
   }
   $taskCode=$taskProcess.ExitCode
  }finally{$null=$taskCopy.GetAwaiter().GetResult();$null=$taskErrorCopy.GetAwaiter().GetResult();$taskLog.Dispose();$taskErr.Dispose();$taskProcess.Dispose()}
  $taskExit=@{started=$taskBegin.ToString('o');completed=[DateTimeOffset]::UtcNow.ToString('o');mode=$taskMode;configuration=$taskConfig.configuration;exitCode=$taskCode;output=$taskOutput;testDirectory=$taskBinary;bindingCaptured=(Test-Path $taskBinding);productionApplied=$false;AFComplete=$false}
  $taskExit|ConvertTo-Json|Set-Content (Join-Path $taskOutput 'exit.json')
  if(!(Test-Path $taskBinding)-or !(Test-Path (Join-Path $taskOutput 'runtime.trx'))){throw 'Missing complete test/native evidence; preserve all output'}
  $taskRows+=@($taskExit);Get-Content (Join-Path $taskOutput 'stdout.log') -Tail 4
 }
}
@{started=$taskStarted.ToString('o');completed=[DateTimeOffset]::UtcNow.ToString('o');configurations=$taskRows;originalRaw90FailuresChanged=$false;originalStrictSTEP24Changed=$false;productionApplied=$false;AFComplete=$false}|ConvertTo-Json -Depth 6|Set-Content $taskSuite
& 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' -X utf8 (Join-Path $taskRoot 'tools/FiniteSurfaceBranchCandidate/audit-runtime.py')
if($LASTEXITCODE -ne 0){throw 'Candidate runtime audit failed; retain all results'}
