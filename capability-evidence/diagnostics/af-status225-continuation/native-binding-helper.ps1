function Save-A5NativeBinding {
    param([int]$ProcessId,[string]$NativeDirectory,$Expected,[string]$OutputPath)
    if (Test-Path -LiteralPath $OutputPath) { return $true }
    $taskNames = @($Expected | ForEach-Object name)
    try { $taskProcess = Get-Process -Id $ProcessId -ErrorAction Stop; $taskModules = @($taskProcess.Modules) }
    catch { return $false }
    $taskLoaded = @($taskModules | Where-Object ModuleName -In $taskNames)
    if ($taskLoaded.Count -ne 62) { return $false }
    $taskWanted = @{}
    foreach ($taskRow in $Expected) { $taskWanted[$taskRow.name] = $taskRow.sha256 }
    $taskRows = @()
    foreach ($taskModule in ($taskLoaded | Sort-Object ModuleName)) {
        $taskPath = [IO.Path]::GetFullPath($taskModule.FileName)
        if ([IO.Path]::GetDirectoryName($taskPath) -cne [IO.Path]::GetFullPath($NativeDirectory)) { throw ('Native loaded outside frozen runtime: '+$taskPath) }
        $taskHash = (Get-FileHash -LiteralPath $taskPath).Hash
        if ($taskHash -cne $taskWanted[$taskModule.ModuleName]) { throw ('Loaded native hash mismatch: '+$taskModule.ModuleName) }
        $taskRows += [ordered]@{ name=$taskModule.ModuleName; path=$taskPath; sha256=$taskHash }
    }
    [ordered]@{ recordedUtc=[DateTimeOffset]::UtcNow.ToString('O'); processId=$ProcessId; processName=$taskProcess.ProcessName; native=$taskRows } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding utf8
    return $true
}
