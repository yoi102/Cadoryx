# Exclude only the user-deferred displayed cases. Do not modify test attributes.
function ConvertTo-CadTestFilterValue {
 param([Parameter(Mandatory)][string]$Value)
 return [regex]::Replace($Value,'([\\()&|=!~])','\$1')
}
function Get-CadDeferredStepNames {
 param([Parameter(Mandatory)][string]$TaskRoot)
 $taskPolicy=Get-Content -LiteralPath (Join-Path $TaskRoot 'capability-evidence/diagnostics/af1081-local86/step24-user-deferral.json') -Raw|ConvertFrom-Json
 $taskNames=@($taskPolicy.exactTestNames)
 if($taskPolicy.state -cne 'USER_DEFERRED_EXACT_24_KNOWN_STRICT_STEP_FAILURES_NOT_PASSED' -or $taskPolicy.deferredCount -ne 24 -or $taskNames.Count -ne 24 -or @($taskNames|Sort-Object -Unique).Count -ne 24){throw 'Exact user-deferred24 policy required'}
 return $taskNames
}
function New-CadDeferredStepFilter {
 param([Parameter(Mandatory)][string]$TaskRoot,[Parameter(Mandatory)][string[]]$DiscoveredNames)
 $taskDeferred=@(Get-CadDeferredStepNames -TaskRoot $TaskRoot)
 $taskDiscovered=[Collections.Generic.Dictionary[string,int]]::new([StringComparer]::Ordinal)
 foreach($taskName in $DiscoveredNames){if($taskDiscovered.ContainsKey($taskName)){$taskDiscovered[$taskName]++}else{$taskDiscovered.Add($taskName,1)}}
 foreach($taskName in $taskDeferred){if(!$taskDiscovered.ContainsKey($taskName) -or $taskDiscovered[$taskName] -ne 1){throw 'Current discovery is missing or ambiguously repeats an exact deferred case; do not guess or exclude a method'}}
 return ($taskDeferred|ForEach-Object {'DisplayName!='+(ConvertTo-CadTestFilterValue $_)}) -join '&'
}
function Assert-CadDeferredStepResults {
 param([Parameter(Mandatory)][string]$TaskRoot,[Parameter(Mandatory)][string[]]$DiscoveredNames,[Parameter(Mandatory)][string]$TrxPath,[Parameter(Mandatory)][int]$ExitCode)
 $null=New-CadDeferredStepFilter -TaskRoot $TaskRoot -DiscoveredNames $DiscoveredNames
 if($ExitCode -ne 0){throw 'Nondeferred regression process failed; inspect actual failures'}
 $taskDeferred=[Collections.Generic.HashSet[string]]::new([string[]]@(Get-CadDeferredStepNames -TaskRoot $TaskRoot),[StringComparer]::Ordinal)
 $taskExpected=[Collections.Generic.Dictionary[string,int]]::new([StringComparer]::Ordinal)
 foreach($taskName in $DiscoveredNames){if(!$taskDeferred.Contains($taskName)){if($taskExpected.ContainsKey($taskName)){$taskExpected[$taskName]++}else{$taskExpected.Add($taskName,1)}}}
 [xml]$taskTrx=Get-Content -LiteralPath $TrxPath -Raw
 $taskRows=@($taskTrx.TestRun.Results.UnitTestResult)
 $taskActual=[Collections.Generic.Dictionary[string,int]]::new([StringComparer]::Ordinal)
 $taskIds=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
 foreach($taskRow in $taskRows){if($taskRow.outcome -cne 'Passed' -or !$taskIds.Add($taskRow.testId)){throw 'Every nondeferred case must pass exactly once'}; if($taskActual.ContainsKey($taskRow.testName)){$taskActual[$taskRow.testName]++}else{$taskActual.Add($taskRow.testName,1)}}
 if($taskActual.Count -ne $taskExpected.Count){throw 'Executed names differ from current discovery minus exactly the user-deferred24'}
 foreach($taskName in $taskExpected.Keys){if(!$taskActual.ContainsKey($taskName) -or $taskActual[$taskName] -ne $taskExpected[$taskName]){throw 'Executed name multiplicities differ from current discovery minus exactly the user-deferred24'}}
}
