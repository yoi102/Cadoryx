. 'C:/Users/yoiri/source/repos/Cadoryx/artifacts/assert-cad-native-consumers-idle-2.ps1'
function Wait-AFNativeConsumersIdle {
 param([Parameter(Mandatory=$true)][string]$EvidenceDirectory)
 $taskSeen=@{}
 while($true){
  try{return Assert-CadNativeConsumersIdle}
  catch{
   $taskReason=$_.Exception.Message
   if($taskReason -notmatch 'PID ([0-9]+)'){throw}
   $taskBusyPid=[int]$Matches[1]
   $taskBusy=Get-CimInstance Win32_Process -Filter ('ProcessId='+$taskBusyPid) -ErrorAction Stop
   if($taskBusy){
    $taskIdentity=$taskBusyPid.ToString()+'-'+$taskBusy.CreationDate.Ticks.ToString()
    if(!$taskSeen.ContainsKey($taskIdentity)){
     [IO.Directory]::CreateDirectory($EvidenceDirectory)|Out-Null
     $taskRecord=Join-Path $EvidenceDirectory ('busy-'+$taskIdentity+'.json')
     @{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');pid=$taskBusyPid;creationDate=$taskBusy.CreationDate;name=$taskBusy.Name;commandLine=$taskBusy.CommandLine;reason=$taskReason;action='PRESERVE_AND_WAIT_FOR_ORIGINAL_GUARD_TO_PASS';nativeStarted=$false}|ConvertTo-Json|Set-Content $taskRecord
     $taskSeen[$taskIdentity]=$true
     Write-Output ('Native lane waiting on observed PID='+$taskBusyPid+'; original guard retained, process preserved.')
    }
   }
   Start-Sleep -Seconds 2
  }
 }
}
