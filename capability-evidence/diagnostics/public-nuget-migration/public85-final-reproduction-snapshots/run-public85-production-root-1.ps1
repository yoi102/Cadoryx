param(
 [Parameter(Mandatory)][string]$PublicVerificationPath,
 [string]$ConsumerOutputDirectory
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$cadRoot=Split-Path -Parent $PSScriptRoot
$cadOutput=if($ConsumerOutputDirectory){[IO.Path]::GetFullPath($ConsumerOutputDirectory)}else{Join-Path $cadRoot 'artifacts/public85-final1073-1'}
$cadPrep=Get-Content -LiteralPath (Join-Path $cadOutput 'preparation.json') -Raw|ConvertFrom-Json
$cadPublic=Get-Content -LiteralPath $PublicVerificationPath -Raw|ConvertFrom-Json
if($cadPublic.state -cne 'PASS_FRESH_PUBLIC_ONLY_14_PACKAGE_LOCKED_RESTORE_EXACT_NATIVE_DISK_PAYLOAD_RUNTIME_NOT_RUN' -or
   $cadPublic.version -cne $cadPrep.version -or $cadPublic.source -cne 'https://api.nuget.org/v3/index.json'){
 throw 'A completed fresh public-only publisher-bound restore is required before compiling this workspace'
}
$cadWorkspace=$cadPrep.workspace
foreach($cadInput in $cadPrep.preparedInputFiles){
 if((Get-FileHash -LiteralPath (Join-Path $cadWorkspace $cadInput.path)).Hash -cne $cadInput.sha256){
  throw ('Prepared input changed: '+$cadInput.path)
 }
}
$cadSupport=Get-Content -LiteralPath (Join-Path $cadOutput 'support-inputs.json') -Raw|ConvertFrom-Json
foreach($cadInput in $cadSupport.files){
 if((Get-FileHash -LiteralPath (Join-Path $cadWorkspace $cadInput.path)).Hash -cne $cadInput.sha256){
  throw ('Prepared support input changed: '+$cadInput.path)
 }
}
$cadPublicCopy=Join-Path $cadOutput 'public-restore-verification.json'
if(Test-Path -LiteralPath $cadPublicCopy){throw 'Fresh immutable consumer attempt required'}
Copy-Item -LiteralPath $PublicVerificationPath -Destination $cadPublicCopy
$cadNative=@($cadPublic.native|Where-Object name -CEQ 'OcctSharp.Native.dll')
if($cadNative.Count -ne 1){throw 'One verified public native bridge required'}
$cadBaselinePath=Join-Path $cadWorkspace 'Cadoryx.wpf/runtime-baseline.json'
$cadBaseline=Get-Content -LiteralPath $cadBaselinePath -Raw|ConvertFrom-Json
if($cadBaseline.occtSharpPackage -cne $cadPublic.version -or $cadBaseline.nativeAbi -cne $cadPublic.nativeAbi -or $cadBaseline.bridgeVersion -cne $cadPublic.bridgeVersion -or $cadBaseline.nativeSha256.ToUpperInvariant() -cne $cadNative[0].sha256){throw 'Applied root baseline differs from verified public payload'}
$cadCache=Join-Path $cadOutput 'fresh-packages'
if(Test-Path -LiteralPath $cadCache){throw 'Fresh public-only package cache required'}
$cadConfig=Join-Path $cadWorkspace 'NuGet.Config'
$cadTargets=@('Cadoryx.slnx')+@($cadPrep.projects|Where-Object {$_ -like 'tools/*' -or $_ -like 'integrations/*'})
if($cadTargets.Count -ne 30){throw 'Expected solution21 plus29 standalone consumers'}
$cadRows=@();$cadStarted=[DateTimeOffset]::UtcNow
$cadPreviousCliLanguage=$env:DOTNET_CLI_UI_LANGUAGE
$env:DOTNET_CLI_UI_LANGUAGE='en'
Push-Location -LiteralPath $cadWorkspace
try{
 $cadSdkVersion=(& dotnet --version).Trim()
 if($LASTEXITCODE -or $cadSdkVersion -cne '10.0.401'){throw 'The isolated stable SDK10.0.401 selection must apply to actual dotnet processes'}
 foreach($cadTarget in $cadTargets){
  $cadName=[IO.Path]::GetFileNameWithoutExtension($cadTarget)
  foreach($cadStage in @('restore','restore-locked','Release','Debug')){
   $cadPrefix=$cadName+'-'+$cadStage.ToLowerInvariant()
   $cadLog=Join-Path $cadOutput ($cadPrefix+'.log')
   if(Test-Path -LiteralPath $cadLog){throw 'Immutable consumer stage log exists'}
   $cadPath=Join-Path $cadWorkspace $cadTarget
   $cadArgs=if($cadStage -like 'restore*'){
    @('restore',$cadPath,'--configfile',$cadConfig,'--packages',$cadCache,'--no-cache','--nologo','-v','minimal','-p:DisableImplicitLibraryPacksFolder=true')
   }else{@('build',$cadPath,'-c',$cadStage,'--no-restore','--nologo','-v','minimal')}
   if($cadTarget -cne 'Cadoryx.slnx'){
    $cadConfiguration=if($cadStage -in @('Release','Debug')){$cadStage}else{'Release'}
    $cadFrozen=Join-Path $cadWorkspace ('Cadoryx.wpf/bin/'+$cadConfiguration+'/net10.0-windows/win-x64')
    $cadArgs+=@("-p:FrozenCadoryx=$cadFrozen","-p:OffsetFrozenDir=$cadFrozen")
   }
   if($cadStage -ceq 'restore-locked'){$cadArgs+=@('--locked-mode')}
   & dotnet @cadArgs > $cadLog 2> (Join-Path $cadOutput ($cadPrefix+'-stderr.log'))
   $cadCode=$LASTEXITCODE
   $cadRow=@{target=$cadTarget;stage=$cadStage;exitCode=$cadCode;log=$cadLog;arguments=$cadArgs;recordedAt=[DateTimeOffset]::UtcNow.ToString('o')}
   $cadRows+=@($cadRow)
   $cadRow|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $cadOutput ($cadPrefix+'-exit.json')) -Encoding utf8
   if($cadCode -ne 0){throw ('Public consumer stage failed: '+$cadPrefix+' / '+$cadCode)}
  }
 }
 $cadUsageDirs=@()
 foreach($cadConfiguration in @('Release','Debug')){
  foreach($cadProject in @('Cadoryx.wpf','Cadoryx.Tests','Cadoryx.Cli')){
   $cadSuffix=if($cadProject -ceq 'Cadoryx.wpf'){'net10.0-windows/win-x64'}else{'net10.0'}
   $cadUsageDirs+=@(Join-Path $cadWorkspace ($cadProject+'/bin/'+$cadConfiguration+'/'+$cadSuffix))
  }
 }
 $cadAudit=Join-Path $cadWorkspace 'tools/OcctApiUsageAudit/bin/Release/net10.0/OcctApiUsageAudit.dll'
 & dotnet $cadAudit (Join-Path $cadOutput 'compiled-occt-usage.json') @cadUsageDirs > (Join-Path $cadOutput 'compiled-occt-usage.log') 2> (Join-Path $cadOutput 'compiled-occt-usage-stderr.log')
 if($LASTEXITCODE){throw 'Compiled PE reference audit failed'}
 @{started=$cadStarted.ToString('o');completed=[DateTimeOffset]::UtcNow.ToString('o');state='PASS_PUBLIC_CONSUMER_AND_TOOLS_50_PROJECTS_RESTORES_BOTH_BUILDS_PENDING_INDEPENDENT_AUDIT';sdkVersion=$cadSdkVersion;stages=$cadRows;workspace=$cadWorkspace;publicVerificationSha256=(Get-FileHash -LiteralPath $cadPublicCopy).Hash;freshCache=$cadCache;runtimeExecuted=$false;productionApplied=$false;AF='ACTIVE';G='USER_DEFERRED'}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $cadOutput 'exit.json') -Encoding utf8
}catch{
 @{started=$cadStarted.ToString('o');recordedAt=[DateTimeOffset]::UtcNow.ToString('o');error=$_.ToString();completedStages=$cadRows;runtimeExecuted=$false;productionApplied=$false;AF='ACTIVE'}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $cadOutput 'failure.json') -Encoding utf8
 throw
}finally{
 Pop-Location
 $env:DOTNET_CLI_UI_LANGUAGE=$cadPreviousCliLanguage
}
