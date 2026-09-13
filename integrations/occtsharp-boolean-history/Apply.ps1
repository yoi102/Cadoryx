[CmdletBinding()]
param([string]$Repository='C:\Users\yoiri\source\repos\OcctSharp', [switch]$CheckOnly)
$ErrorActionPreference='Stop'
$cadUpstream=(Resolve-Path -LiteralPath $Repository).Path.TrimEnd('\','/')
$cadChanges=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'changes.json') -Raw | ConvertFrom-Json
$cadPrepared=@()
foreach($cadChange in $cadChanges){
    $cadTarget=[IO.Path]::GetFullPath((Join-Path $cadUpstream $cadChange.path))
    if(!$cadTarget.StartsWith($cadUpstream+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Target leaves the requested repository.'}
    if($cadChange.path -match '(^|/)(generated|Generated|\.git)/'){throw 'Generated output and Git metadata cannot be changed.'}
    $cadBytes=[Convert]::FromBase64String($cadChange.content)
    if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($cadBytes)) -ne $cadChange.after){throw "Payload hash mismatch: $($cadChange.path)"}
    if(Test-Path -LiteralPath $cadTarget){
        $cadActual=(Get-FileHash -LiteralPath $cadTarget -Algorithm SHA256).Hash
        if($cadActual -eq $cadChange.after){continue}
        if(!$cadChange.before -or $cadActual -ne $cadChange.before){throw "Upstream file changed; refusing overwrite: $($cadChange.path)"}
    }elseif($cadChange.before){throw "Missing original file: $($cadChange.path)"}
    $cadPrepared+=@{Path=$cadTarget;Bytes=$cadBytes}
}
if($CheckOnly){Write-Output "Patch preconditions passed: $($cadPrepared.Count) files";return}
foreach($cadFile in $cadPrepared){
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($cadFile.Path)) | Out-Null
    [IO.File]::WriteAllBytes($cadFile.Path,$cadFile.Bytes)
}
Write-Output "Applied $($cadPrepared.Count) upstream files. No generated files changed."
