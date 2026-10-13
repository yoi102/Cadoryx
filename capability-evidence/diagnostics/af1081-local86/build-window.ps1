$ErrorActionPreference='Stop'
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx'
$taskDraft=Join-Path $taskRoot 'artifacts/af1081-local86-1'
$taskSource=Join-Path $taskDraft 'window-observer-1'
$taskProof=Get-Content (Join-Path $taskDraft 'performance-freeze.json') -Raw|ConvertFrom-Json
$taskStages=@()
foreach($taskConfig in @('Release','Debug')){
 $taskCandidate=@($taskProof.configurations|Where-Object configuration -CEQ $taskConfig)[0]
 foreach($taskRow in $taskCandidate.payload){if((Get-FileHash -LiteralPath (Join-Path $taskCandidate.candidate $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Frozen WPF payload differs'}}
 $taskLog=Join-Path $taskDraft ('window-observer-1-'+$taskConfig.ToLower()+'.log')
 $taskStart=[Diagnostics.ProcessStartInfo]::new((Get-Command dotnet).Source)
 $taskStart.WorkingDirectory=$taskSource;$taskStart.UseShellExecute=$false;$taskStart.CreateNoWindow=$true
 $taskStart.RedirectStandardOutput=$true;$taskStart.RedirectStandardError=$true
 foreach($taskArg in @('build','WindowHost.csproj','-c',$taskConfig,('-p:CandidatePayload='+$taskCandidate.candidate))){$taskStart.ArgumentList.Add($taskArg)}
 $taskProcess=[Diagnostics.Process]::Start($taskStart)
 $taskOut=$taskProcess.StandardOutput.ReadToEndAsync();$taskErr=$taskProcess.StandardError.ReadToEndAsync()
 $taskProcess.WaitForExit();$taskCode=$taskProcess.ExitCode
 [IO.File]::WriteAllText($taskLog,$taskOut.GetAwaiter().GetResult()+$taskErr.GetAwaiter().GetResult())
 $taskStages+=@{configuration=$taskConfig;exitCode=$taskCode;log=$taskLog;candidate=$taskCandidate.candidate}
 $taskProcess.Dispose()
 @{stages=$taskStages;nativeStarted=$false;desktopInputStarted=$false;productionDialogChanged=$false}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $taskDraft 'window-observer-1-build-exit.json')
 Get-Content $taskLog -Tail 5
 if($taskCode -ne 0){throw 'Observer build failed; original output preserved'}
}
