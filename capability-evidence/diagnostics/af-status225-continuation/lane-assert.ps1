# Observe known CAD consumers and dotnet-hosted native workflows without stopping any process.
function Assert-CadNativeConsumersIdle {
 $taskObservations=@()
 $taskNames=@('testhost','Cadoryx.wpf','Cadoryx.Cli','CadAgentWorkflowProbe','GuidedCopyConsumer','GuidedHistoryConsumer',
  'MaterialAuthoringConsumer','ReferenceConsumer','SourceConsumer','ViewportConsumer','SerializedReferenceConsumer','dotnet')
 $taskCandidates=@(Get-Process -Name $taskNames -ErrorAction SilentlyContinue)
 # Include executable consumers from either shared CAD checkout, even when
 # their application name is new (for example a public SDK M6 consumer).
 foreach($taskAdditional in @(Get-CimInstance Win32_Process -ErrorAction Stop|Where-Object {$_.ExecutablePath -match '[\\/]Cadoryx[\\/]|[\\/]OcctSharp[\\/]'})){
  $taskAdditionalProcess=Get-Process -Id $taskAdditional.ProcessId -ErrorAction SilentlyContinue
  if($taskAdditionalProcess){$taskCandidates+=@($taskAdditionalProcess)}
 }
 foreach($taskObserved in @($taskCandidates|Sort-Object Id -Unique)){
  try{
   $taskPath=$taskObserved.Path
   $taskNativeModules=@($taskObserved.Modules|Where-Object {$_.ModuleName -eq 'OcctSharp.Native.dll' -or $_.ModuleName -eq 'TKBRep.dll'})
   $taskCommand=$null
   if($taskObserved.ProcessName -eq 'dotnet'){
    $taskMetadata=Get-CimInstance Win32_Process -Filter ('ProcessId='+$taskObserved.Id) -ErrorAction Stop
    if($taskMetadata){$taskCommand=$taskMetadata.CommandLine}
   }
  }catch{
   if($taskObserved.HasExited){continue}
   throw ('CAD/OCCT consumer state unavailable; preserve it: PID '+$taskObserved.Id)
  }
  $taskDotnetCad=$taskObserved.ProcessName -eq 'dotnet' -and $taskCommand -and
   ($taskCommand -match '[\\/]Cadoryx[\\/]|[\\/]OcctSharp[\\/]|CadAgentWorkflowProbe\.dll|Cadoryx\.Cli\.dll')
  if(!$taskPath -or $taskPath -match '[\\/]Cadoryx[\\/]|[\\/]OcctSharp[\\/]' -or $taskNativeModules.Count -gt 0 -or $taskDotnetCad){
   throw ('CAD/OCCT consumer already running; preserve it: PID '+$taskObserved.Id)
  }
  $taskObservations+=@([ordered]@{pid=$taskObserved.Id;path=$taskPath;occtModules=@($taskNativeModules|ForEach-Object FileName);action='PRESERVED_UNRELATED_PROCESS'})
 }
 return $taskObservations
}
