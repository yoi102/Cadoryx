param(
 [ValidateSet('d6-sdk30-production-integration-1','b18-prescribed-complete-authors-draft-2','d6-finite-newbody-draft-4')][Parameter(Mandatory)][string]$Draft,
 [ValidateSet('Release','Debug')][Parameter(Mandatory)][string]$Configuration,
 [ValidateSet('finite99','agent165','full6530','full6556','full6619')][Parameter(Mandatory)][string]$Group,
 [ValidateRange(1,2)][Parameter(Mandatory)][int]$Attempt
)
$ErrorActionPreference='Stop'
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx'
$taskDraft=Join-Path $taskRoot ('artifacts/'+$Draft)
$taskProof=Get-Content -LiteralPath (Join-Path $taskDraft 'build-both-1-verification.json') -Raw|ConvertFrom-Json
$taskCfg=$taskProof.configurations.$Configuration
if($Draft -eq 'd6-sdk30-production-integration-1' -and $Group -notin @('finite99','agent165','full6530')){throw 'SDK30 scope differs'}
if($Draft -eq 'b18-prescribed-complete-authors-draft-2' -and $Group -ne 'full6556'){throw 'Solver21 scope differs'}
if($Draft -eq 'd6-finite-newbody-draft-4' -and $Group -ne 'full6619'){throw 'SDK31 D6 scope differs'}
$taskRun=Join-Path $taskDraft ($Group+'-'+$Configuration.ToLower()+'-'+$Attempt)
if(Test-Path -LiteralPath $taskRun){throw 'Immutable runtime directory exists'}
foreach($taskRow in $taskProof.sourcePayload){if((Get-FileHash -LiteralPath (Join-Path $taskProof.workspace $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Frozen source drift'}}
foreach($taskRow in $taskCfg.testPayload){if((Get-FileHash -LiteralPath (Join-Path $taskCfg.testDirectory $taskRow.path)).Hash -cne $taskRow.sha256){throw 'Frozen runtime drift'}}
. (Join-Path $taskRoot 'artifacts/wait-cad-native-consumers-idle-1.ps1')
$null=Wait-CadNativeConsumersIdle
New-Item -ItemType Directory -Path $taskRun|Out-Null
$taskFilter=switch($Group){
 'finite99'{'FullyQualifiedName~FiniteSurfaceExtrusionTests|FullyQualifiedName~FiniteSurfaceNewBodyTests'}
 'agent165'{'FullyQualifiedName~AgentIdentityFieldPresentationTests|FullyQualifiedName~ToolPresentationTests|FullyQualifiedName~AgentSuccessfulActionReceiptTests|FullyQualifiedName~AgentEmptyResponseRecoveryTests|FullyQualifiedName~DrawingInspectionTypeFeedbackTests|FullyQualifiedName~DrawingContractRequestGuideTests|FullyQualifiedName~DrawingAgentTests'}
 default {'FullyQualifiedName~Cadoryx.Tests'}
}
. (Join-Path $taskRoot 'artifacts/a5-native-binding-helper-1.ps1')
$taskStart=[Diagnostics.ProcessStartInfo]::new((Get-Command dotnet).Source)
$taskStart.WorkingDirectory=$taskProof.workspace
$taskStart.UseShellExecute=$false
$taskStart.CreateNoWindow=$true
$taskStart.WindowStyle='Hidden'
$taskStart.RedirectStandardOutput=$true
$taskStart.RedirectStandardError=$true
$taskStart.Environment['DOTNET_CLI_UI_LANGUAGE']='en'
foreach($taskArg in @('test','Cadoryx.Tests/Cadoryx.Tests.csproj','-c',$Configuration,'--no-build','--no-restore','--filter',$taskFilter,'--logger','console;verbosity=normal','--logger','trx;LogFileName=runtime.trx','--results-directory',$taskRun,'--nologo','-v','minimal')){$taskStart.ArgumentList.Add($taskArg)}
$taskStarted=[DateTimeOffset]::UtcNow
$taskProcess=[Diagnostics.Process]::Start($taskStart)
$taskProcess.PriorityClass='BelowNormal'
$taskPid=$taskProcess.Id
$taskOut=[IO.File]::Open((Join-Path $taskRun 'stdout.log'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
$taskErr=[IO.File]::Open((Join-Path $taskRun 'stderr.log'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
$taskCopy=$taskProcess.StandardOutput.BaseStream.CopyToAsync($taskOut)
$taskErrorCopy=$taskProcess.StandardError.BaseStream.CopyToAsync($taskErr)
$taskBinding=Join-Path $taskRun 'loaded-native.json'
try{
 while(!$taskProcess.WaitForExit(100)){
  if(!(Test-Path -LiteralPath $taskBinding)){
   foreach($taskHost in @(Get-Process testhost -ErrorAction SilentlyContinue)){
    if($taskHost.Path -eq (Join-Path $taskCfg.testDirectory 'testhost.exe')){
     $null=Save-A5NativeBinding -ProcessId $taskHost.Id -NativeDirectory (Join-Path $taskCfg.testDirectory 'occt') -Expected $taskProof.native62 -OutputPath $taskBinding
    }
   }
  }
 }
 $taskCode=$taskProcess.ExitCode
}finally{
 $null=$taskCopy.GetAwaiter().GetResult()
 $null=$taskErrorCopy.GetAwaiter().GetResult()
 $taskOut.Dispose()
 $taskErr.Dispose()
 $taskProcess.Dispose()
}
@{started=$taskStarted.ToString('o');completed=[DateTimeOffset]::UtcNow.ToString('o');pid=$taskPid;exitCode=$taskCode;configuration=$Configuration;group=$Group;attempt=$Attempt;binding=$taskBinding;runtimeDirectory=$taskCfg.testDirectory;productionApplied=$false;desktopInputStarted=$false;cleanup=$false;originalBudgetsChanged=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $taskRun 'exit.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $taskRun 'stdout.log') -Tail 8
& 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' -X utf8 (Join-Path $PSScriptRoot 'runtime-audit.py') --draft $Draft --configuration $Configuration --group $Group --attempt $Attempt
if($LASTEXITCODE -ne 0){throw 'Runtime audit failed; all original outputs preserved'}
exit $taskCode
