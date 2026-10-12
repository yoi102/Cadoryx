$ErrorActionPreference='Stop'
$cadOutput='C:/Users/yoiri/source/repos/Cadoryx/artifacts/public-occt-preview85-consumer1073-1'
$cadWorkspace=Join-Path $cadOutput 'workspace'
$cadCache=Join-Path $cadOutput 'fresh-packages'
$cadStarted=[DateTimeOffset]::UtcNow
$cadStages=@()
function Invoke-CadMigrationStage {
 param([string]$Name,[string[]]$Arguments)
 $cadLog=Join-Path $cadOutput ($Name+'.log')
 if(Test-Path -LiteralPath $cadLog){throw 'Immutable migration stage log exists'}
 & dotnet @Arguments > $cadLog 2> (Join-Path $cadOutput ($Name+'-stderr.log'))
 $cadCode=$LASTEXITCODE
 @{stage=$Name;exitCode=$cadCode;recordedAt=[DateTimeOffset]::UtcNow.ToString('o');arguments=$Arguments;nativeCodeExecuted=$false;productionApplied=$false;publicSourceOnly=$false}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $cadOutput ($Name+'-exit.json')) -Encoding utf8
 if($cadCode -ne 0){throw ('Migration stage failed: '+$Name+' / '+$cadCode)}
}
try{
 $cadSolution=Join-Path $cadWorkspace 'Cadoryx.slnx'
 $cadConfig=Join-Path $cadWorkspace 'NuGet.Config'
 Invoke-CadMigrationStage -Name 'restore' -Arguments @('restore',$cadSolution,'--configfile',$cadConfig,'--packages',$cadCache,'--nologo','-v','minimal')
 $cadStages+=@('restore')
 Invoke-CadMigrationStage -Name 'restore-locked' -Arguments @('restore',$cadSolution,'--configfile',$cadConfig,'--packages',$cadCache,'--locked-mode','--nologo','-v','minimal')
 $cadStages+=@('restore-locked')
 foreach($cadConfiguration in @('Release','Debug')){
  $cadStage='build-'+$cadConfiguration.ToLowerInvariant()
  Invoke-CadMigrationStage -Name $cadStage -Arguments @('build',$cadSolution,'-c',$cadConfiguration,'--no-restore','--nologo','-v','minimal')
  $cadStages+=@($cadStage)
 }
 @{started=$cadStarted.ToString('o');completed=[DateTimeOffset]::UtcNow.ToString('o');state='PASS_ISOLATED_SOURCE1073_LOCAL14_ORDINARY_LOCKED_RESTORE_BOTH_MANAGED_BUILDS_PENDING_INDEPENDENT_PAYLOAD_AUDIT';completedStages=$cadStages;workspace=$cadWorkspace;freshCache=$cadCache;runtimeExecuted=$false;publicationConfirmed=$false;vcpkgProfileAccepted=$false;productionApplied=$false;cleanup=$false;AF='ACTIVE';G='USER_DEFERRED'}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $cadOutput 'exit.json') -Encoding utf8
}catch{
 @{started=$cadStarted.ToString('o');recordedAt=[DateTimeOffset]::UtcNow.ToString('o');error=$_.ToString();completedStages=$cadStages;workspace=$cadWorkspace;runtimeExecuted=$false;publicationConfirmed=$false;productionApplied=$false;cleanup=$false;AF='ACTIVE'}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $cadOutput 'failure.json') -Encoding utf8
 throw
}
