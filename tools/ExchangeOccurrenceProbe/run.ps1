[CmdletBinding()]
param([ValidateSet('Source','External','Returned')][string]$Action='Source',[ValidateRange(1,100)][int]$Iteration=1)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskStage=Join-Path $taskRoot 'artifacts/f4-occurrence-external-1'
$taskPrep=Get-Content (Join-Path $taskStage 'preparation.json') -Raw|ConvertFrom-Json -DateKind String
if((Get-FileHash (Join-Path $taskRoot $taskPrep.applicationFreeze.path)).Hash -cne $taskPrep.applicationFreeze.sha256){throw 'Application freeze differs from the compiled consumer source'}
& 'C:/Users/yoiri/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' (Join-Path $taskRoot 'tools/FinalCadAcceptance/audit-full.py') 8
if($LASTEXITCODE -ne 0){throw 'Current full candidate8 required before independent F4 runtime'}
$taskQueue=Join-Path $taskRoot 'artifacts/af-final87-8/after-full-exit.json'
if(!(Test-Path $taskQueue) -or !(Get-Content $taskQueue -Raw|ConvertFrom-Json).modelsTerminal){throw 'Original candidate8 model queue is not terminal; preserve its lane'}
. (Join-Path $taskRoot 'tools/CurrentCadCandidate/require-current-return.ps1')
Assert-CurrentSdkNativeReturn -TaskRoot $taskRoot
. (Join-Path $taskRoot 'artifacts/assert-cad-native-consumers-idle-2.ps1')
$taskUnrelated=@(Assert-CadNativeConsumersIdle)
foreach($taskFile in $taskPrep.source+$taskPrep.payload){if((Get-FileHash (Join-Path $taskRoot $taskFile.path)).Hash -cne $taskFile.sha256){throw 'Frozen external consumer or collector changed'}}
$taskOutput=Join-Path $taskStage ('runtime-'+$Action.ToLower()+'-'+$Iteration)
$taskSourceDirectory=Join-Path $taskStage ('source-'+$Iteration)
$taskExternalDirectory=Join-Path $taskStage ('freecad-'+$Iteration)
if(Test-Path ($taskOutput+'.start.json')){throw 'Preserve original external runtime attempt'}
@{started=[DateTimeOffset]::UtcNow.ToString('o');coordinatorPid=$PID;action=$Action;iteration=$Iteration;coordinatorSHA256=(Get-FileHash $PSCommandPath).Hash;preparationSHA256=(Get-FileHash (Join-Path $taskStage 'preparation.json')).Hash;unrelatedPreserved=$taskUnrelated;AFComplete=$false}|ConvertTo-Json -Depth 5|Set-Content ($taskOutput+'.start.json')
$taskConsumer=Join-Path $taskPrep.consumerPublished 'ExchangeOccurrenceProbe.dll'
$taskCode=1;$taskFailure=$null
try{
if($Action -eq 'Source'){
 & dotnet $taskConsumer make $taskSourceDirectory *> ($taskOutput+'.log')
 $taskCode=$LASTEXITCODE
}elseif($Action -eq 'External'){
 $taskSource=Join-Path $taskSourceDirectory 'source.step'
 if(!(Test-Path $taskSource)){throw 'Actual native source is missing'}
 & (Join-Path $taskRoot 'tools/MulticopyExchangeProbe/run-freecad.ps1') -Script (Join-Path $PSScriptRoot 'freecad_observe.py') -Arguments @($taskSource,$taskExternalDirectory) -OutputPrefix $taskOutput -TimeoutSeconds 180
 $taskCode=$LASTEXITCODE
}else{
 $taskExternal=Get-Content (Join-Path $taskExternalDirectory 'observation.json') -Raw|ConvertFrom-Json
 $taskExpectedReturn=Join-Path $taskExternalDirectory 'freecad-return.step'
 if(!$taskExternal.observationCompleted -or [IO.Path]::GetFullPath($taskExternal.returned) -cne [IO.Path]::GetFullPath($taskExpectedReturn) -or (Get-FileHash $taskExpectedReturn).Hash -cne $taskExternal.returnedSha256){throw 'Completed actual external return with exact bytes required'}
 & dotnet $taskConsumer inspect (Join-Path $taskStage ('returned-native-'+$Iteration+'.json')) $taskExpectedReturn *> ($taskOutput+'.log')
 $taskCode=$LASTEXITCODE
}
}catch{$taskFailure=$_.ToString();$taskCode=1}
finally{
 @{completed=[DateTimeOffset]::UtcNow.ToString('o');action=$Action;iteration=$Iteration;exitCode=$taskCode;error=$taskFailure;preparationSHA256=(Get-FileHash (Join-Path $taskStage 'preparation.json')).Hash;fullInteroperabilityAccepted=$false;AFComplete=$false}|ConvertTo-Json|Set-Content ($taskOutput+'.exit.json')
}
if($taskCode -ne 0){throw 'Actual F4 stage failed; preserve source/output/log and repair before a new attempt'}
