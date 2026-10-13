$ErrorActionPreference='Stop'
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx'
$taskDraft=Join-Path $taskRoot 'artifacts/af1081-local86-1'
$taskWorkspace=Join-Path $taskDraft 'workspace'
$taskPython='C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe'
$taskAudit=Join-Path $taskRoot 'capability-evidence/diagnostics/af1081-local86/prepare.py'
$taskStages=@()
function Invoke-CandidateStage([string]$stage,[string[]]$arguments) {
 $taskLog=Join-Path $taskDraft ($stage+'.log')
 if(Test-Path -LiteralPath $taskLog){throw 'Immutable stage exists; inspect original run'}
 $taskStart=[Diagnostics.ProcessStartInfo]::new((Get-Command dotnet).Source)
 $taskStart.WorkingDirectory=$taskWorkspace;$taskStart.UseShellExecute=$false;$taskStart.CreateNoWindow=$true;$taskStart.WindowStyle='Hidden'
 $taskStart.Environment['NUGET_PACKAGES']=Join-Path $taskDraft 'packages'
 $taskStart.RedirectStandardOutput=$true;$taskStart.RedirectStandardError=$true
 foreach($taskArgument in $arguments){$taskStart.ArgumentList.Add($taskArgument)}
 $taskProcess=[Diagnostics.Process]::Start($taskStart)
 @{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');coordinatorPid=$PID;stage=$stage;processId=$taskProcess.Id;arguments=$arguments;nativeRuntime=$false}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $taskDraft ($stage+'-start.json'))
 Write-Output ($stage+' PID='+$taskProcess.Id)
 $taskOut=$taskProcess.StandardOutput.ReadToEndAsync();$taskErr=$taskProcess.StandardError.ReadToEndAsync()
 $taskProcess.WaitForExit();$taskCode=$taskProcess.ExitCode
 [IO.File]::WriteAllText($taskLog,$taskOut.GetAwaiter().GetResult()+$taskErr.GetAwaiter().GetResult())
 $taskProcess.Dispose()
 Get-Content -LiteralPath $taskLog -Tail 5
 if($taskCode -ne 0){throw ('Candidate stage failed; original output preserved: '+$stage)}
 return @{stage=$stage;exitCode=$taskCode;log=$taskLog}
}
# Return stage rows separately from progress output.
foreach($taskStage in @('restore-update','restore-locked','release','debug','provider-publish','discovery-release','discovery-debug')) {
 $taskArguments=switch($taskStage){
  'restore-update' {@('restore','CandidateAllProjects.slnx','--force-evaluate')}
  'restore-locked' {@('restore','CandidateAllProjects.slnx','--locked-mode')}
  'release' {@('build','Cadoryx.slnx','--no-restore','-c','Release')}
  'debug' {@('build','Cadoryx.slnx','--no-restore','-c','Debug')}
  'provider-publish' {@('publish','tools/CadAgentWorkflowProbe/CadAgentWorkflowProbe.csproj','--no-restore','-c','Release','-p:BuildProjectReferences=false','--self-contained','false','-o',(Join-Path $taskDraft 'provider'))}
  'discovery-release' {@('vstest','Cadoryx.Tests/bin/Release/net10.0/Cadoryx.Tests.dll','/ListTests','/TestCaseFilter:FullyQualifiedName~Cadoryx.Tests')}
  'discovery-debug' {@('vstest','Cadoryx.Tests/bin/Debug/net10.0/Cadoryx.Tests.dll','/ListTests','/TestCaseFilter:FullyQualifiedName~Cadoryx.Tests')}
 }
 $taskResult=@(Invoke-CandidateStage $taskStage $taskArguments)
 $taskResult|Where-Object {$_ -is [string]}|ForEach-Object {Write-Output $_}
 $taskStages+=@($taskResult|Where-Object {$_ -is [hashtable]})
 @{stages=$taskStages;productionApplied=$false;publiclyPublished=$false;actualFullTestRun=$false}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $taskDraft 'build-exit.json')
 if($taskStage -eq 'restore-update') {
  & $taskPython -X utf8 $taskAudit locks
  if($LASTEXITCODE -ne 0){throw 'Non-OCCT lock preservation audit failed'}
 }
}
& $taskPython -X utf8 $taskAudit freeze
if($LASTEXITCODE -ne 0){throw 'Candidate freeze audit failed'}
