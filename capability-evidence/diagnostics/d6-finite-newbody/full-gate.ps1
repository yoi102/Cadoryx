param([ValidateSet('Release','Debug')][Parameter(Mandatory)][string]$Configuration,[ValidateSet('full6619')][Parameter(Mandatory)][string]$Group)
$ErrorActionPreference='Stop'
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx'
$taskDraft=Join-Path $taskRoot 'artifacts/d6-finite-newbody-draft-4'
$taskProof=Get-Content -LiteralPath (Join-Path $taskDraft 'build-both-1-verification.json') -Raw|ConvertFrom-Json
$taskCfg=$taskProof.configurations.$Configuration
$taskFilter='FullyQualifiedName~Cadoryx.Tests'
$taskRun=Join-Path $taskDraft ($Group+'-'+$Configuration.ToLower()+'-1');if(Test-Path -LiteralPath $taskRun){throw 'Immutable runtime evidence exists'}
New-Item -ItemType Directory -Path $taskRun|Out-Null
foreach($taskRow in $taskProof.sourcePayload){if((Get-FileHash -LiteralPath (Join-Path $taskProof.workspace $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Frozen source1073 D6 solver21 SDK31 drift'}}
foreach($taskRow in $taskCfg.testPayload){if((Get-FileHash -LiteralPath (Join-Path $taskCfg.testDirectory $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Runtime payload drift'}}
. (Join-Path $taskRoot 'artifacts/wait-cad-native-consumers-idle-1.ps1')
$null=Wait-CadNativeConsumersIdle
. (Join-Path $taskRoot 'artifacts/a5-native-binding-helper-1.ps1')
$taskStart=[Diagnostics.ProcessStartInfo]::new((Get-Command dotnet).Source)
$taskStart.WorkingDirectory=$taskProof.workspace;$taskStart.UseShellExecute=$false;$taskStart.CreateNoWindow=$true;$taskStart.WindowStyle='Hidden'
$taskStart.RedirectStandardOutput=$true;$taskStart.RedirectStandardError=$true;$taskStart.Environment['DOTNET_CLI_UI_LANGUAGE']='en'
foreach($taskArg in @('test','Cadoryx.Tests/Cadoryx.Tests.csproj','-c',$Configuration,'--no-build','--no-restore','--filter',$taskFilter,'--logger','console;verbosity=normal','--logger','trx;LogFileName=runtime.trx','--results-directory',$taskRun,'--nologo','-v','minimal')){$taskStart.ArgumentList.Add($taskArg)}
$taskStarted=[DateTimeOffset]::UtcNow
$taskProcess=[Diagnostics.Process]::Start($taskStart)
$taskOut=[IO.File]::Open((Join-Path $taskRun 'stdout.log'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
$taskErr=[IO.File]::Open((Join-Path $taskRun 'stderr.log'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
$taskCopy=$taskProcess.StandardOutput.BaseStream.CopyToAsync($taskOut);$taskErrorCopy=$taskProcess.StandardError.BaseStream.CopyToAsync($taskErr)
$taskBinding=Join-Path $taskRun 'loaded-native.json'
try{
 while(!$taskProcess.WaitForExit(100)){
  if(!(Test-Path -LiteralPath $taskBinding)){
   foreach($taskHost in @(Get-Process testhost -ErrorAction SilentlyContinue)){
    if($taskHost.Path -eq (Join-Path $taskCfg.testDirectory 'testhost.exe')){$null=Save-A5NativeBinding -ProcessId $taskHost.Id -NativeDirectory (Join-Path $taskCfg.testDirectory 'occt') -Expected $taskProof.native62 -OutputPath $taskBinding}
   }
  }
 }
 $taskCode=$taskProcess.ExitCode
}finally{
 $null=$taskCopy.GetAwaiter().GetResult();$null=$taskErrorCopy.GetAwaiter().GetResult();$taskOut.Dispose();$taskErr.Dispose();$taskProcess.Dispose()
}
@{started=$taskStarted.ToString('o');completed=[DateTimeOffset]::UtcNow.ToString('o');exitCode=$taskCode;configuration=$Configuration;group=$Group;binding=$taskBinding;runtimeDirectory=$taskCfg.testDirectory;productionApplied=$false;desktopInputStarted=$false;cleanup=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $taskRun 'exit.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $taskRun 'stdout.log') -Tail 8
Write-Output ('Actual native binding present: '+(Test-Path -LiteralPath $taskBinding)+'; exit='+$taskCode)
exit $taskCode
