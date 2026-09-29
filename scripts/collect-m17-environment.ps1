param(
    [Parameter(Mandatory=$true)][string]$Installer,
    [string[]]$Samples=@(),
    [string]$Output,
    [switch]$RunInstallSmoke
)
$ErrorActionPreference='Stop'
$cadInstaller=(Resolve-Path -LiteralPath $Installer).Path
$cadBundle=Split-Path -Parent $cadInstaller
$cadManifestPath=Join-Path $cadBundle 'manifest.json'
$cadManifest=Get-Content -LiteralPath $cadManifestPath -Raw | ConvertFrom-Json
$cadHash=(Get-FileHash -LiteralPath $cadInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
if($cadHash -ne $cadManifest.msiSha256){throw 'Installer hash does not match its manifest.'}
$cadOutput=if($Output){[IO.Path]::GetFullPath($Output)}else{
    Join-Path $cadBundle ('m17-environment-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
}
if(Test-Path -LiteralPath $cadOutput){throw 'Choose a new evidence directory.'}
$cadOs=Get-CimInstance Win32_OperatingSystem
$cadCpu=Get-CimInstance Win32_Processor | Select-Object -First 1
$cadDisplays=@(Get-CimInstance Win32_VideoController | ForEach-Object {
    [ordered]@{name=$_.Name;driverVersion=$_.DriverVersion;resolution="$($_.CurrentHorizontalResolution)x$($_.CurrentVerticalResolution)"}
})
$cadMonitors=@(Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorBasicDisplayParams -ErrorAction SilentlyContinue |
    ForEach-Object {[ordered]@{instance=$_.InstanceName;active=$_.Active;horizontalCm=$_.MaxHorizontalImageSize;verticalCm=$_.MaxVerticalImageSize}})
$cadSamples=@(foreach($sample in $Samples){
    $path=(Resolve-Path -LiteralPath $sample).Path
    $file=Get-Item -LiteralPath $path
    if($file.PSIsContainer){throw "Expected a model file: $path"}
    [ordered]@{name=$file.Name;bytes=$file.Length;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
})
New-Item -ItemType Directory -Path $cadOutput | Out-Null
$cadSignature=Get-AuthenticodeSignature -LiteralPath $cadInstaller
$cadDotnet=if(Get-Command dotnet -ErrorAction SilentlyContinue){@(dotnet --list-sdks 2>$null)}else{@()}
$cadRecord=[ordered]@{
    schemaVersion=1
    capturedUtc=[DateTimeOffset]::UtcNow.ToString('o')
    installer=[ordered]@{name=[IO.Path]::GetFileName($cadInstaller);bytes=(Get-Item -LiteralPath $cadInstaller).Length
        sha256=$cadHash;version=$cadManifest.version;signatureStatus=[string]$cadSignature.Status
        signer=if($cadSignature.SignerCertificate){$cadSignature.SignerCertificate.Subject}else{$null}}
    machine=[ordered]@{name=$env:COMPUTERNAME;os=$cadOs.Caption;osVersion=$cadOs.Version
        build=$cadOs.BuildNumber;architecture=$cadOs.OSArchitecture;cpu=$cadCpu.Name
        totalMemoryBytes=[long]$cadOs.TotalVisibleMemorySize*1024;session=$env:SESSIONNAME
        videoControllers=$cadDisplays;monitors=$cadMonitors;dotnetSdks=$cadDotnet}
    samples=$cadSamples
    installSmoke='NOT_RUN'
    physicalDpi='NOT_RUN'
    rdpReconnect='NOT_RUN'
    multiGpu='NOT_RUN'
    longInteraction='NOT_RUN'
    physicalPrint='NOT_RUN'
    mediaFault='NOT_RUN'
    externalDistribution='NOT_RUN'
}
if($RunInstallSmoke){
    $cadSmokeOutput=Join-Path $cadOutput 'install-smoke'
    & (Join-Path $PSScriptRoot 'verify-installer.ps1') -Installer $cadInstaller -Output $cadSmokeOutput
    $cadRecord.installSmoke='PASS'
}
$cadRecord | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $cadOutput 'environment.json') -Encoding utf8
Write-Output "Evidence: $cadOutput"
