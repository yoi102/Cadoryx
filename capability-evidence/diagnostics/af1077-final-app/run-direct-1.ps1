[CmdletBinding()]
param([int]$Runs=2,[int]$Cycles=20)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx'
$taskInput='C:/Users/yoiri/Downloads/MiniPC3.step'
$taskHash='F3773A7F37BD0CB8E717E4BE6F1070A72E5C6E7B2F36F26F9501183DF8C4C68E'
. (Join-Path $taskRoot 'artifacts/assert-af1077-performance-ready-1.ps1')
. (Join-Path $taskRoot 'artifacts/assert-cad-native-consumers-idle-2.ps1')
$null=Assert-CadNativeConsumersIdle
$taskOutput=Join-Path $taskRoot 'artifacts/af1077-direct-1'
if(Test-Path -LiteralPath $taskOutput){throw 'Choose a new direct benchmark iteration.'}
if($Runs -ne 2 -or $Cycles -ne 20){throw 'This fixed evidence plan uses two fresh processes and the original twenty cycles.'}
if(Get-Process testhost,Cadoryx.wpf -ErrorAction SilentlyContinue){throw 'Native consumers and tests must be serialized.'}
if((Get-FileHash -LiteralPath $taskInput).Hash -ne $taskHash){throw 'Fixed MiniPC3 input changed.'}
foreach($taskRow in $taskFreeze.payload){
 if((Get-FileHash -LiteralPath (Join-Path $taskFreeze.candidate $taskRow.path)).Hash -ne $taskRow.sha256){throw ('Frozen payload drift: '+$taskRow.path)}
}
[IO.Directory]::CreateDirectory($taskOutput)|Out-Null
$taskReports=@()
for($taskRun=1;$taskRun -le $Runs;$taskRun++){
 $taskRunOutput=Join-Path $taskOutput ('run-'+$taskRun)
 [IO.Directory]::CreateDirectory($taskRunOutput)|Out-Null
 $taskStart=[Diagnostics.ProcessStartInfo]::new((Join-Path $taskFreeze.candidate 'Cadoryx.wpf.exe'))
 $taskStart.WorkingDirectory=$taskFreeze.candidate
 $taskStart.UseShellExecute=$false;$taskStart.CreateNoWindow=$true
 $taskStart.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
 foreach($taskArg in @('--m6-benchmark',$taskInput,$taskRunOutput,$Cycles.ToString())){$taskStart.ArgumentList.Add($taskArg)}
 $taskProcess=[Diagnostics.Process]::Start($taskStart)
 Write-Output ('Direct run '+$taskRun+' PID='+$taskProcess.Id+'; original1800-second deadline; two viewports and20 cycles.')
 $taskDeadline=[DateTime]::UtcNow.AddSeconds(1800)
 $taskNativeRecorded=$false
 try{
  while(!$taskProcess.WaitForExit(1000)){
   if([DateTime]::UtcNow -gt $taskDeadline){$taskProcess.Kill($true);throw 'Owned direct benchmark exceeded original1800-second deadline.'}
   if(!$taskNativeRecorded){
    $taskObserved=Get-Process -Id $taskProcess.Id -ErrorAction Stop
    $taskModules=@($taskObserved.Modules|Where-Object {$taskFreeze.native.name -contains $_.ModuleName})
    if($taskModules.Count -eq 62){
     $taskRows=@($taskModules|ForEach-Object {
      $taskExpected=$taskFreeze.native|Where-Object name -eq $_.ModuleName
      $taskDigest=(Get-FileHash -LiteralPath $_.FileName).Hash
      if($taskDigest -ne $taskExpected.sha256 -or [IO.Path]::GetDirectoryName($_.FileName) -ne (Join-Path $taskFreeze.candidate 'occt')){throw ('Actual native module mismatch: '+$_.ModuleName)}
      [ordered]@{name=$_.ModuleName;path=$_.FileName;sha256=$taskDigest}
     })
     [ordered]@{recordedUtc=[DateTime]::UtcNow.ToString('O');pid=$taskProcess.Id;files=$taskRows}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $taskRunOutput 'loaded-native.json') -Encoding utf8
     $taskNativeRecorded=$true
    }
   }
  }
  $taskResultPath=Join-Path $taskRunOutput 'result.txt'
  if(!(Test-Path -LiteralPath $taskResultPath)){throw ('Direct exited without result: '+$taskProcess.ExitCode)}
  $taskResult=Get-Content -LiteralPath $taskResultPath -Raw
  if($taskProcess.ExitCode -ne 0 -or !$taskResult.StartsWith('PASS:')){throw ('Direct run failed: '+$taskResult)}
  if(!$taskNativeRecorded){throw 'Direct process exited before actual62 native paths were recorded.'}
  $taskReport=Get-Content -LiteralPath (Join-Path $taskRunOutput 'user-model-1-report.json') -Raw|ConvertFrom-Json
  if($taskReport.sourceSha256 -ne $taskHash.ToLowerInvariant() -or $taskReport.sourceBytes -ne 88258425 -or $taskReport.visibleInstances -ne 4964){throw 'Direct model hash/bytes/instance identity changed.'}
  if(@($taskReport.lifecycle).Count -ne 20){throw 'Twenty-cycle lifecycle evidence missing.'}
  foreach($taskCycle in $taskReport.lifecycle){
   if($taskCycle.primaryGeometry -ne 1185 -or $taskCycle.secondaryGeometry -ne 1185 -or $taskCycle.primaryXdeContexts -ne 1 -or $taskCycle.secondaryXdeContexts -ne 1 -or $taskCycle.primarySources -ne 1185 -or $taskCycle.secondarySources -ne 1185){throw 'Direct geometry/context/source identity mismatch.'}
  }
  if((Get-Item -LiteralPath (Join-Path $taskRunOutput 'bindings.log')).Length -ne 0){throw 'Direct WPF binding errors.'}
  $taskReports+=@{run=$taskRun;report=Join-Path $taskRunOutput 'user-model-1-report.json';firstSubmitMs=$taskReport.firstSubmitMs;firstVisibleSubmitMs=$taskReport.firstVisibleSubmitMs;inputServiceP95Ms=$taskReport.inputServiceP95Ms;targetMs=6482;targetMet=$taskReport.firstSubmitMs -le 6482}
  Write-Output ('Direct run '+$taskRun+' PASS; first complete='+$taskReport.firstSubmitMs+'ms; target6482ms remains independent of correctness PASS.')
 }
 finally{
  if(!$taskProcess.HasExited){$taskProcess.Kill($true);$taskProcess.WaitForExit()}
  $taskProcess.Dispose()
 }
}
$taskSummary=[ordered]@{state='CORRECTNESS_PASS_PERFORMANCE_AS_RECORDED';sourceSha256=$taskHash;sourceBytes=88258425;visibleInstances=4964;geometries=1185;runs=$taskReports;
 cpu=@(Get-CimInstance Win32_Processor|Select-Object Name,NumberOfCores,NumberOfLogicalProcessors);
 gpu=@(Get-CimInstance Win32_VideoController|Select-Object Name,DriverVersion);physicalMemoryBytes=(Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory;
 cacheCondition='Fresh application processes; OS file cache, antivirus and background load not controlled';A5='NOT_ACCEPTED';G='DEFERRED_BY_USER'}
$taskSummary|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $taskOutput 'summary.json') -Encoding utf8
Write-Output 'Two fixed direct/lifecycle runs completed; performance is reported, never inferred from process PASS.'
