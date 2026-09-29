param(
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [Parameter(Mandatory=$true)][string]$Output,
    [ValidateRange(1,1440)][int]$Minutes=120,
    [ValidateRange(1,300)][int]$IntervalSeconds=30
)
$ErrorActionPreference='Stop'
$cadOutput=[IO.Path]::GetFullPath($Output)
if(Test-Path -LiteralPath $cadOutput){throw 'Choose a new CSV path to preserve prior evidence.'}
$cadProcess=Get-Process -Id $ProcessId -ErrorAction Stop
if($cadProcess.ProcessName -ne 'Cadoryx.wpf'){throw 'The selected process is not Cadoryx.wpf.'}
$cadStarted=$cadProcess.StartTime
$cadEnd=[DateTimeOffset]::UtcNow.AddMinutes($Minutes)
$cadRows=[Collections.Generic.List[object]]::new()
do{
    $cadProcess=Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if($null -eq $cadProcess){break}
    if($cadProcess.StartTime -ne $cadStarted){throw 'Process ID was reused during sampling.'}
    $cadRows.Add([pscustomobject]@{
        utc=[DateTimeOffset]::UtcNow.ToString('o')
        workingSetBytes=$cadProcess.WorkingSet64
        privateBytes=$cadProcess.PrivateMemorySize64
        handles=$cadProcess.HandleCount
        cpuSeconds=$cadProcess.CPU
        threads=$cadProcess.Threads.Count
    })
    if([DateTimeOffset]::UtcNow -ge $cadEnd){break}
    Start-Sleep -Seconds $IntervalSeconds
}while($true)
if($cadRows.Count -eq 0){throw 'No process samples were captured.'}
$cadDirectory=Split-Path -Parent $cadOutput
New-Item -ItemType Directory -Path $cadDirectory -Force | Out-Null
$cadRows | Export-Csv -LiteralPath $cadOutput -NoTypeInformation -Encoding utf8
Write-Output "Captured $($cadRows.Count) resource samples: $cadOutput"
