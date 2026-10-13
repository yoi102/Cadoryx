$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskFocused=Join-Path $taskRoot 'artifacts/d7-analytic-branch-draft-4/focused-verification.json'
$taskDeadline=[DateTimeOffset]::UtcNow.AddMinutes(60)
Write-Output 'Waiting for both complete 243-case focused terminal audits; no native runtime started here'
while(!(Test-Path $taskFocused)){
 if([DateTimeOffset]::UtcNow -gt $taskDeadline){throw 'Focused terminal evidence not received; preserve all output'}
 Start-Sleep -Seconds 5
}
$taskProof=Get-Content $taskFocused -Raw|ConvertFrom-Json -DateKind String
if(!$taskProof.candidateFocusedPassed){throw 'Focused candidate failed; do not advance application qualification'}
foreach($taskConfig in $taskProof.configurations){
 if($taskConfig.total -ne 243 -or $taskConfig.failed -ne 0 -or $taskConfig.skipped -ne 0){throw 'Incomplete focused candidate'}
}
. (Join-Path $taskRoot 'artifacts/assert-cad-native-consumers-idle-2.ps1')
$null=Assert-CadNativeConsumersIdle
& (Join-Path $PSScriptRoot 'build.ps1')
