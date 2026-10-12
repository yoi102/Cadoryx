# A busy native lane is a scheduling dependency, never permission to terminate.
. (Join-Path $PSScriptRoot 'assert-cad-native-consumers-idle-2.ps1')
function Wait-CadNativeConsumersIdle {
 param([double]$MaximumHours=6)
 $taskLaneWait=[Diagnostics.Stopwatch]::StartNew()
 $taskLastBusy=$null
 while($true){
  # The user's new order prioritizes public SDK migration/publication. Only
  # future consumers wait here; a consumer already past this gate is untouched.
  $taskPriority=Join-Path $PSScriptRoot 'native-lane-priority-preview85-1.json'
  $taskPriorityRelease=Join-Path $PSScriptRoot 'native-lane-priority-preview85-1-release.json'
  if((Test-Path -LiteralPath $taskPriority)-and !(Test-Path -LiteralPath $taskPriorityRelease)){
   $taskPriorityLease=Get-Content -LiteralPath $taskPriority -Raw|ConvertFrom-Json
   if($taskPriorityLease.requesterThread -ne '01a0f7b3-ff5f-7cb0-bff9-a5caad1a49b0'){throw 'Unexpected public SDK priority requester'}
   $taskPriorityUntil=[DateTimeOffset]::Parse($taskPriorityLease.until)
   if([DateTimeOffset]::UtcNow -lt $taskPriorityUntil){
    if($taskLastBusy -ne 'PREVIEW85_PRIORITY'){Write-Output ('Next native lane reserved for public SDK migration until '+$taskPriorityUntil.ToString('o')+'; original consumer preserved.');$taskLastBusy='PREVIEW85_PRIORITY'}
    if($taskLaneWait.Elapsed.TotalHours -gt $MaximumHours){throw 'Public SDK priority lane remains busy; original consumers preserved'}
    Start-Sleep -Seconds 5;continue
   }
  }
  # Reserve the next idle lane for the other SDK performance run. No running
  # consumer is stopped and no original native/tool/test budget is altered.
  $taskReservation=Join-Path $PSScriptRoot 'native-lane-reservation-preview83-1.json'
  $taskActivation=Join-Path $PSScriptRoot 'native-lane-reservation-preview83-1-activation.json'
  $taskRelease=Join-Path $PSScriptRoot 'native-lane-reservation-preview83-1-release.json'
  if((Test-Path -LiteralPath $taskReservation)-and !(Test-Path -LiteralPath $taskRelease)){
   $taskRequest=Get-Content -LiteralPath $taskReservation -Raw|ConvertFrom-Json
   if($taskRequest.minutes -ne 30){throw 'Unexpected bounded native lane reservation'}
   if(Test-Path -LiteralPath $taskActivation){
    $taskLease=Get-Content -LiteralPath $taskActivation -Raw|ConvertFrom-Json
    $taskUntil=[DateTimeOffset]::Parse($taskLease.until)
    $taskExtension=Join-Path $PSScriptRoot 'native-lane-reservation-preview83-1-extension-1.json'
    if(Test-Path -LiteralPath $taskExtension){
     $taskExtended=Get-Content -LiteralPath $taskExtension -Raw|ConvertFrom-Json
     if($taskExtended.requesterThread -ne $taskRequest.requesterThread){throw 'Native lane extension requester differs'}
     $taskExtendedUntil=[DateTimeOffset]::Parse($taskExtended.until)
     if($taskExtendedUntil -gt $taskUntil){$taskUntil=$taskExtendedUntil}
    }
    if([DateTimeOffset]::UtcNow -lt $taskUntil){
     if($taskLastBusy -ne 'PREVIEW83_RESERVED'){Write-Output ('Native lane reserved for Preview.83 performance until '+$taskUntil.ToString('o')+'; original consumer preserved.');$taskLastBusy='PREVIEW83_RESERVED'}
     if($taskLaneWait.Elapsed.TotalHours -gt $MaximumHours){throw 'Existing native lane reservation remains busy; original consumers preserved'}
     Start-Sleep -Seconds 5;continue
    }
   }else{
    # Activate only when the current full suite/provider actually finishes.
    # The existing assertion below still verifies real live consumers.
    try{
     $taskObservedIdle=@(Assert-CadNativeConsumersIdle)
     $taskActivated=[DateTimeOffset]::UtcNow
     $taskLeaseRecord=@{activatedAt=$taskActivated.ToString('o');until=$taskActivated.AddMinutes($taskRequest.minutes).ToString('o');reservationSha256=(Get-FileHash -LiteralPath $taskReservation).Hash;idleObserved=$taskObservedIdle;originalConsumersStopped=$false;cleanup=$false}|ConvertTo-Json -Depth 5
     $taskLeaseTemp=$taskActivation+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
     [IO.File]::WriteAllText($taskLeaseTemp,$taskLeaseRecord,[Text.UTF8Encoding]::new($false))
     try{[IO.File]::Move($taskLeaseTemp,$taskActivation,$false)}catch [IO.IOException]{if(!(Test-Path -LiteralPath $taskActivation)){throw}}
     continue
    }catch{
     if($_.Exception.Message -notlike 'CAD/OCCT consumer already running; preserve it: PID *'){throw}
    }
   }
  }
  try{return @(Assert-CadNativeConsumersIdle)}
  catch{
   $taskBusy=$_.Exception.Message
   if($taskBusy -notlike 'CAD/OCCT consumer already running; preserve it: PID *'){throw}
   if($taskBusy -cne $taskLastBusy){Write-Output ('Waiting for existing native lane; '+$taskBusy);$taskLastBusy=$taskBusy}
   if($taskLaneWait.Elapsed.TotalHours -gt $MaximumHours){throw 'Existing native lane remains busy; all original consumers preserved'}
   Start-Sleep -Seconds 5
  }
 }
}
