$ErrorActionPreference='Stop'
$taskRoot='C:/Users/yoiri/source/repos/Cadoryx'
$taskHelper=Join-Path $taskRoot 'artifacts/wait-cad-native-consumers-idle-1.ps1'
$taskSaved=Join-Path $taskRoot 'artifacts/native-lane-priority-preview85-1'
if(Test-Path -LiteralPath $taskSaved){throw 'Immutable priority scheduling preparation exists'}
New-Item -ItemType Directory -Path $taskSaved | Out-Null
Copy-Item -LiteralPath $taskHelper -Destination (Join-Path $taskSaved 'wait-helper-before.ps1')
$taskBefore=(Get-FileHash -LiteralPath $taskHelper).Hash
$taskText=Get-Content -LiteralPath $taskHelper -Raw
$taskInsertion=@'
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
'@
$taskNeedle=' while($true){'
if(($taskText.Split($taskNeedle)).Count -ne 2){throw 'Original scheduling loop differs'}
$taskText=$taskText.Replace($taskNeedle,$taskNeedle+[Environment]::NewLine+$taskInsertion)
$taskTemp=$taskHelper+'.preview85-'+[Guid]::NewGuid().ToString('N')+'.tmp'
[IO.File]::WriteAllText($taskTemp,$taskText,[Text.UTF8Encoding]::new($false))
$taskParserTokens=$null
$taskParserErrors=$null
$null=[Management.Automation.Language.Parser]::ParseFile($taskTemp,[ref]$taskParserTokens,[ref]$taskParserErrors)
if($taskParserErrors.Count){throw 'Priority scheduling helper syntax failed; original preserved'}
$taskReservation=Join-Path $taskRoot 'artifacts/native-lane-priority-preview85-1.json'
if(Test-Path -LiteralPath $taskReservation){throw 'Immutable initial public priority reservation exists'}
@{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');until=[DateTimeOffset]::UtcNow.AddHours(3).ToString('o');requesterThread='01a0f7b3-ff5f-7cb0-bff9-a5caad1a49b0';purpose='User-prioritized necessary generated OCCT API migration and public NuGet delivery';state='HOLD_ONLY_NEXT_CAD_NATIVE_CONSUMER_CURRENT_FULL6556_RELEASE_UNINTERRUPTED';currentDependency='artifacts/b18-prescribed-complete-authors-draft-2/full6556-release-2/exit.json';originalMaximumWaitHours=6;originalTestAndNativeBudgetsChanged=$false;originalScopesChanged=$false;cleanup=$false;AF='ACTIVE'}|ConvertTo-Json|Set-Content -LiteralPath $taskReservation -Encoding utf8
[IO.File]::Replace($taskTemp,$taskHelper,(Join-Path $taskSaved 'wait-helper-replace-backup.ps1'))
Copy-Item -LiteralPath $taskHelper -Destination (Join-Path $taskSaved 'wait-helper-after.ps1')
@{recordedAt=[DateTimeOffset]::UtcNow.ToString('o');originalHelperSha256=$taskBefore;newHelperSha256=(Get-FileHash -LiteralPath $taskHelper).Hash;reservationSha256=(Get-FileHash -LiteralPath $taskReservation).Hash;frozenRunGateAndQueueChanged=$false;productSourcesChanged=$false;currentConsumerStopped=$false;originalBudgetsChanged=$false;oldPreview83ReservationChanged=$false;cleanup=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $taskSaved 'preparation.json') -Encoding utf8
Write-Output 'PASS prioritized next idle lane for public SDK; current full suite and all original scope/budgets preserved.'
