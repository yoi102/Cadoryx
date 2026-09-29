param([string]$ReportPath)

$ErrorActionPreference = 'Stop'
$cadRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $cadStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $cadResults = Join-Path $cadRoot "artifacts\coverage-$cadStamp"
    Push-Location $cadRoot
    try {
        dotnet test Cadoryx.Tests -c Release --no-restore --nologo -v:q `
            --collect 'Code Coverage;Format=cobertura' --results-directory $cadResults
        if ($LASTEXITCODE -ne 0) { throw 'Coverage test run failed.' }
    }
    finally { Pop-Location }
    $cadReports = @(Get-ChildItem -LiteralPath $cadResults -Recurse -Filter '*.cobertura.xml')
    if ($cadReports.Count -ne 1) { throw "Expected one Cobertura report; found $($cadReports.Count)." }
    $ReportPath = $cadReports[0].FullName
}

$ReportPath = (Resolve-Path -LiteralPath $ReportPath).Path
[xml]$cadXml = Get-Content -LiteralPath $ReportPath -Raw
$cadRows = foreach ($cadPackage in $cadXml.coverage.packages.package) {
    $cadLineValid = 0; $cadLineCovered = 0
    $cadBranchValid = 0; $cadBranchCovered = 0
    foreach ($cadClass in $cadPackage.classes.class) {
        # Cobertura's line-rate uses method lines, including multiple methods on one source line.
        foreach ($cadMethod in $cadClass.methods.method) {
            foreach ($cadLine in $cadMethod.lines.line) {
                $cadLineValid++
                if ([int]$cadLine.hits -gt 0) { $cadLineCovered++ }
            }
        }
        foreach ($cadLine in $cadClass.lines.line) {
            $cadCondition = $cadLine.GetAttribute('condition-coverage')
            if ($cadCondition -match '\((\d+)/(\d+)\)') {
                $cadBranchCovered += [int]$Matches[1]
                $cadBranchValid += [int]$Matches[2]
            }
        }
    }
    [pscustomobject]@{
        Assembly = [string]$cadPackage.name
        LinesCovered = $cadLineCovered
        LinesValid = $cadLineValid
        BranchesCovered = $cadBranchCovered
        BranchesValid = $cadBranchValid
        LinePercent = if ($cadLineValid) { [math]::Round(100 * $cadLineCovered / $cadLineValid, 1) } else { 0 }
        BranchPercent = if ($cadBranchValid) { [math]::Round(100 * $cadBranchCovered / $cadBranchValid, 1) } else { 0 }
    }
}

function Get-CadSummary($cadName, $cadItems) {
    $cadLineValid = ($cadItems | Measure-Object LinesValid -Sum).Sum
    $cadLineCovered = ($cadItems | Measure-Object LinesCovered -Sum).Sum
    $cadBranchValid = ($cadItems | Measure-Object BranchesValid -Sum).Sum
    $cadBranchCovered = ($cadItems | Measure-Object BranchesCovered -Sum).Sum
    [pscustomobject]@{
        Scope = $cadName
        LinesCovered = $cadLineCovered
        LinesValid = $cadLineValid
        LinePercent = [math]::Round(100 * $cadLineCovered / $cadLineValid, 1)
        BranchesCovered = $cadBranchCovered
        BranchesValid = $cadBranchValid
        BranchPercent = [math]::Round(100 * $cadBranchCovered / $cadBranchValid, 1)
    }
}

$cadProduction = @($cadRows | Where-Object { $_.Assembly -like 'Cadoryx.*' -and $_.Assembly -ne 'Cadoryx.Tests' })
$cadHandwritten = @($cadProduction | Where-Object { $_.Assembly -ne 'Cadoryx.Lang' })
$cadSummary = @(
    Get-CadSummary 'Raw Cobertura report' $cadRows
    Get-CadSummary 'Cadoryx production assemblies' $cadProduction
    Get-CadSummary 'Cadoryx excluding generated localization' $cadHandwritten
)
if ($cadSummary[0].LinesValid -ne [int]$cadXml.coverage.'lines-valid' -or
    $cadSummary[0].BranchesValid -ne [int]$cadXml.coverage.'branches-valid') {
    throw 'Cobertura package totals do not match the report root.'
}

$cadResult = [pscustomobject]@{
    Report = $ReportPath
    ScopeNote = 'xUnit-loaded managed assemblies only; WPF executable, native OCCT and desktop smoke are excluded.'
    Summary = $cadSummary
    Assemblies = @($cadProduction | Sort-Object Assembly)
}
$cadSummary | Format-Table -AutoSize | Out-String | Write-Output
$cadResult.Assemblies | Format-Table Assembly, LinePercent, BranchPercent, LinesCovered, LinesValid -AutoSize | Out-String | Write-Output
$cadJson = Join-Path (Split-Path -Parent $ReportPath) 'summary.json'
$cadResult | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $cadJson -Encoding utf8
Write-Output "Cobertura: $ReportPath"
Write-Output "Summary: $cadJson"
