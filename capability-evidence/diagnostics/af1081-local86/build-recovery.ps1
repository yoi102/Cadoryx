$ErrorActionPreference='Stop'
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx'
$taskDraft=Join-Path $taskRoot 'artifacts/af1081-local86-1'
$taskFreeze=Get-Content (Join-Path $taskDraft 'freeze.json') -Raw|ConvertFrom-Json
$taskPreparation=Get-Content (Join-Path $taskDraft 'recovery-preparation.json') -Raw|ConvertFrom-Json
foreach($taskRow in $taskPreparation.sources){if((Get-FileHash (Join-Path $taskRoot $taskRow.candidate.path)).Hash -cne $taskRow.candidate.sha256){throw 'Recovery source differs'}}
$taskStages=@()
foreach($taskConfig in @('Release','Debug')) {
 $taskApp=@($taskFreeze.configurations|Where-Object configuration -CEQ $taskConfig)[0]
 foreach($taskLabel in @('recovery24','main-status36')) {
  $taskSource=Join-Path $taskDraft ($taskLabel+'/source')
  $taskProject=if($taskLabel -eq 'recovery24'){'WpfAutoRecoveryConsumer.csproj'}else{'MainStatusRecovery.csproj'}
  $taskOutput=Join-Path $taskDraft ($taskLabel+'/publish-'+$taskConfig.ToLower())
  $taskLog=Join-Path $taskDraft ($taskLabel+'-'+$taskConfig.ToLower()+'-build.log')
  if((Test-Path $taskOutput)-or(Test-Path $taskLog)){throw 'Immutable recovery stage exists'}
  $taskStart=[Diagnostics.ProcessStartInfo]::new((Get-Command dotnet).Source)
  $taskStart.WorkingDirectory=$taskSource;$taskStart.UseShellExecute=$false;$taskStart.CreateNoWindow=$true;$taskStart.WindowStyle='Hidden'
  $taskStart.RedirectStandardOutput=$true;$taskStart.RedirectStandardError=$true
  foreach($taskArg in @('publish',$taskProject,'-c',$taskConfig,'--self-contained','false',('-p:CandidatePayload='+$taskApp.candidate),'-o',$taskOutput)){$taskStart.ArgumentList.Add($taskArg)}
  $taskProcess=[Diagnostics.Process]::Start($taskStart)
  $taskOut=$taskProcess.StandardOutput.ReadToEndAsync();$taskErr=$taskProcess.StandardError.ReadToEndAsync()
  $taskProcess.WaitForExit();$taskCode=$taskProcess.ExitCode
  [IO.File]::WriteAllText($taskLog,$taskOut.GetAwaiter().GetResult()+$taskErr.GetAwaiter().GetResult())
  $taskProcess.Dispose()
  $taskStages+=@{label=$taskLabel;configuration=$taskConfig;exitCode=$taskCode;log=$taskLog;output=$taskOutput;candidate=$taskApp.candidate}
  @{stages=$taskStages;nativeRuntimeStarted=$false;desktopInputStarted=$false}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $taskDraft 'recovery-build-exit.json')
  Get-Content $taskLog -Tail 5
  if($taskCode -ne 0){throw 'Recovery host build failed; original output preserved'}
 }
}
& 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' -X utf8 (Join-Path $taskRoot 'capability-evidence/diagnostics/af1081-local86/prepare-recovery.py') audit-build
if($LASTEXITCODE -ne 0){throw 'Recovery build identity audit failed'}
