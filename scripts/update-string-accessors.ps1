# Synchronize newly added .resx keys with the checked-in VS resource accessor class.
# Antelcat generates LangKeys from these properties, not directly from .resx files.
$ErrorActionPreference = 'Stop'
$cadRoot = Split-Path -Parent $PSScriptRoot
$cadDesignerPath = Join-Path $cadRoot 'Cadoryx.Lang/Strings/Strings.Designer.cs'
$cadResourcePath = Join-Path $cadRoot 'Cadoryx.Lang/Strings/Strings.resx'
[xml]$cadResource = Get-Content -LiteralPath $cadResourcePath -Raw
$cadDesigner = [IO.File]::ReadAllText($cadDesignerPath)
$cadProperties = [regex]::Matches($cadDesigner, 'public static string (\w+)\s*(?:\{|=>)') | ForEach-Object { $_.Groups[1].Value }
$cadMissing = @($cadResource.root.data.name | Where-Object { $_ -notin $cadProperties } | Sort-Object)
if ($cadMissing.Count -eq 0) { Write-Output 'Resource accessors are current.'; return }
$cadLines = foreach ($cadKey in $cadMissing) {
    if ($cadKey -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') { throw "Unsupported C# resource identifier: $cadKey" }
    '        public static string ' + $cadKey + ' => ResourceManager.GetString("' + $cadKey + '", resourceCulture);'
}
$cadEnding = [regex]::Match($cadDesigner, '\s*}\s*}\s*$')
if (!$cadEnding.Success) { throw 'Resource class closing braces not found.' }
$cadDesigner = $cadDesigner.Substring(0, $cadEnding.Index) + "`r`n" + ($cadLines -join "`r`n") + "`r`n    }`r`n}`r`n"
[IO.File]::WriteAllText($cadDesignerPath, $cadDesigner)
Write-Output "Added $($cadMissing.Count) resource accessors."
