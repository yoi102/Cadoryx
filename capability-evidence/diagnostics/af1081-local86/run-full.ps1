$ErrorActionPreference='Stop'
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx'
$taskDraft=Join-Path $taskRoot 'artifacts/af1081-local86-1'
$taskProof=Get-Content (Join-Path $taskDraft 'freeze.json') -Raw|ConvertFrom-Json
$taskWorkspace=$taskProof.workspace
$taskTerminal=Join-Path $taskDraft 'full-both-exit.json'
if(Test-Path $taskTerminal){throw 'Immutable full run exists'}
$taskHandoff=Get-Content (Join-Path $taskRoot 'artifacts/local-sdk86-received-1/native-lane-released.json') -Raw|ConvertFrom-Json
if(!$taskHandoff.sdkExplicitHandoffReceived -or $taskHandoff.acceptedLocalPackageVersion -cne $taskProof.version){throw 'Required SDK handoff not received'}
. (Join-Path $taskRoot 'artifacts/assert-cad-native-consumers-idle-2.ps1')
. (Join-Path $taskRoot 'artifacts/a5-native-binding-helper-1.ps1')
$taskRows=@();$taskSuiteStarted=[DateTimeOffset]::UtcNow
foreach($taskConfig in $taskProof.configurations) {
 $null=Assert-CadNativeConsumersIdle
 foreach($taskFile in $taskProof.sourcePayload){if((Get-FileHash (Join-Path $taskWorkspace $taskFile.path)).Hash -cne $taskFile.sha256){throw 'Frozen source differs'}}
 foreach($taskFile in $taskConfig.payload){if((Get-FileHash (Join-Path $taskConfig.testDirectory $taskFile.path)).Hash -cne $taskFile.sha256){throw 'Frozen binary differs'}}
 $taskOutput=Join-Path $taskDraft ('full6830-'+$taskConfig.configuration.ToLower()+'-1')
 if(Test-Path $taskOutput){throw 'Immutable config output exists'}
 New-Item -ItemType Directory -Path $taskOutput | Out-Null
 $taskStart=[Diagnostics.ProcessStartInfo]::new((Get-Command dotnet).Source)
 $taskStart.WorkingDirectory=$taskWorkspace;$taskStart.UseShellExecute=$false;$taskStart.CreateNoWindow=$true;$taskStart.WindowStyle='Hidden'
 $taskStart.RedirectStandardOutput=$true;$taskStart.RedirectStandardError=$true
 foreach($taskArg in @('vstest',(Join-Path $taskConfig.testDirectory 'Cadoryx.Tests.dll'),('/TestCaseFilter:'+$taskProof.fullTestFilter),'/Logger:console;verbosity=normal','/Logger:trx;LogFileName=runtime.trx',('/ResultsDirectory:'+$taskOutput))){$taskStart.ArgumentList.Add($taskArg)}
 $taskStarted=[DateTimeOffset]::UtcNow;$taskProcess=[Diagnostics.Process]::Start($taskStart)
 @{started=$taskStarted.ToString('o');configuration=$taskConfig.configuration;pid=$taskProcess.Id;coordinatorPid=$PID;testDirectory=$taskConfig.testDirectory;scope=6830}|ConvertTo-Json|Set-Content (Join-Path $taskOutput 'start.json')
 Write-Output ('Local86 full6830 '+$taskConfig.configuration+' started PID='+$taskProcess.Id)
 $taskLog=[IO.File]::Open((Join-Path $taskOutput 'stdout.log'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
 $taskErr=[IO.File]::Open((Join-Path $taskOutput 'stderr.log'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
 $taskCopy=$taskProcess.StandardOutput.BaseStream.CopyToAsync($taskLog);$taskErrorCopy=$taskProcess.StandardError.BaseStream.CopyToAsync($taskErr)
 $taskBinding=Join-Path $taskOutput 'loaded-native.json'
 try{
  while(!$taskProcess.WaitForExit(100)){
   if(!(Test-Path $taskBinding)){
    foreach($taskHost in @(Get-Process testhost -ErrorAction SilentlyContinue)){
     if($taskHost.Path -ceq (Join-Path $taskConfig.testDirectory 'testhost.exe')){$null=Save-A5NativeBinding -ProcessId $taskHost.Id -NativeDirectory (Join-Path $taskConfig.testDirectory 'occt') -Expected $taskProof.native62 -OutputPath $taskBinding}
    }
   }
  }
  $taskCode=$taskProcess.ExitCode
 }finally{$null=$taskCopy.GetAwaiter().GetResult();$null=$taskErrorCopy.GetAwaiter().GetResult();$taskLog.Dispose();$taskErr.Dispose();$taskProcess.Dispose()}
 $taskRow=@{configuration=$taskConfig.configuration;exitCode=$taskCode;started=$taskStarted.ToString('o');completed=[DateTimeOffset]::UtcNow.ToString('o');output=$taskOutput;bindingCaptured=(Test-Path $taskBinding)}
 $taskRow|ConvertTo-Json -Depth 5|Set-Content (Join-Path $taskOutput 'exit.json');$taskRows+=$taskRow
 Get-Content (Join-Path $taskOutput 'stdout.log') -Tail 6
 if(!(Test-Path $taskBinding)-or !(Test-Path (Join-Path $taskOutput 'runtime.trx'))){$taskRows|ConvertTo-Json -Depth 6|Set-Content (Join-Path $taskDraft 'incomplete.json');throw 'Terminal lacks actual scope or binding'}
}
@{started=$taskSuiteStarted.ToString('o');completed=[DateTimeOffset]::UtcNow.ToString('o');configurations=$taskRows;expected=6830;productionApplied=$false;publiclyPublished=$false;AFComplete=$false;cleanup=$false;G='USER_DEFERRED'}|ConvertTo-Json -Depth 6|Set-Content $taskTerminal
& 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' -X utf8 (Join-Path $taskRoot 'capability-evidence/diagnostics/af1081-local86/audit-full.py')
if($LASTEXITCODE -ne 0){throw 'Completed full audit failed; original outputs preserved'}
