$ErrorActionPreference='Stop'
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx'
$taskDraft=Join-Path $taskRoot 'artifacts/af1081-local86-1'
$taskProof=Get-Content (Join-Path $taskDraft 'freeze.json') -Raw|ConvertFrom-Json
$taskSource=Join-Path $taskRoot 'tools/CadRecoveryWindowProbe'
$taskStages=@()
foreach($taskConfig in @('Release','Debug')){
 $taskApp=@($taskProof.configurations|Where-Object configuration -CEQ $taskConfig)[0]
 foreach($taskLabel in @('ViewportRecovery','MainStatusRecovery')){
  $taskProject=if($taskLabel -ceq 'ViewportRecovery'){'WpfAutoRecoveryConsumer.csproj'}else{'MainStatusRecovery.csproj'}
  $taskOutput=Join-Path $taskDraft ('durable-recovery/'+$taskLabel+'/'+$taskConfig)
  $taskLog=Join-Path $taskDraft ('durable-'+$taskLabel+'-'+$taskConfig+'.log')
  if((Test-Path $taskOutput)-or(Test-Path $taskLog)){throw 'Immutable durable build exists'}
  $taskStart=[Diagnostics.ProcessStartInfo]::new((Get-Command dotnet).Source)
  $taskStart.WorkingDirectory=Join-Path $taskSource $taskLabel;$taskStart.UseShellExecute=$false;$taskStart.CreateNoWindow=$true;$taskStart.WindowStyle='Hidden'
  $taskStart.RedirectStandardOutput=$true;$taskStart.RedirectStandardError=$true
  foreach($taskArgument in @('publish',$taskProject,'-c',$taskConfig,'--self-contained','false',('-p:CandidatePayload='+$taskApp.candidate),'-o',$taskOutput)){$taskStart.ArgumentList.Add($taskArgument)}
  $taskProcess=[Diagnostics.Process]::Start($taskStart)
  $taskOut=$taskProcess.StandardOutput.ReadToEndAsync();$taskErr=$taskProcess.StandardError.ReadToEndAsync()
  $taskProcess.WaitForExit();$taskCode=$taskProcess.ExitCode
  [IO.File]::WriteAllText($taskLog,$taskOut.GetAwaiter().GetResult()+$taskErr.GetAwaiter().GetResult());$taskProcess.Dispose()
  $taskStages+=@{label=$taskLabel;configuration=$taskConfig;exitCode=$taskCode;output=$taskOutput;log=$taskLog;candidate=$taskApp.candidate}
  @{stages=$taskStages;nativeRuntimeStarted=$false;desktopInputStarted=$false}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $taskDraft 'durable-recovery-build-exit.json')
  Get-Content $taskLog -Tail 4
  if($taskCode -ne 0){throw 'Durable probe publish failed; original output preserved'}
 }
}
