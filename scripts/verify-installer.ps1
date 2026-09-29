param(
    [Parameter(Mandatory=$true)][string]$Installer,
    [string]$Output
)
$ErrorActionPreference='Stop'
$cadMsi=(Resolve-Path -LiteralPath $Installer).Path
$cadPackage=Split-Path -Parent $cadMsi
$cadManifest=Get-Content -LiteralPath (Join-Path $cadPackage 'manifest.json') -Raw | ConvertFrom-Json
$cadOutput=if($Output){[IO.Path]::GetFullPath($Output)}else{
    Join-Path $cadPackage ('verification-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
}
if(Test-Path -LiteralPath $cadOutput){throw 'Choose a new verification output directory.'}
if((Get-FileHash -LiteralPath $cadMsi -Algorithm SHA256).Hash.ToLowerInvariant() -ne $cadManifest.msiSha256){
    throw 'Installer hash differs from the release manifest.'
}
if($cadManifest.signingThumbprint){
    $cadSignature=Get-AuthenticodeSignature -LiteralPath $cadMsi
    if($cadSignature.SignerCertificate.Thumbprint -ne $cadManifest.signingThumbprint){
        throw 'Installer signer differs from the manifest.'
    }
}
function GetCadoryxRegistration {
    foreach($key in @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
                     'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall')){
        Get-ChildItem -Path $key -ErrorAction SilentlyContinue |
            ForEach-Object {Get-ItemProperty $_.PSPath} |
            Where-Object DisplayName -eq 'Cadoryx'
    }
}
if(@(GetCadoryxRegistration).Count){throw 'Cadoryx is already installed; refusing to replace it during verification.'}
$cadInstall=Join-Path $cadOutput 'Installed'
if(Test-Path -LiteralPath $cadInstall){throw 'Test installation target already exists.'}
New-Item -ItemType Directory -Path $cadOutput | Out-Null
$cadLink=Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Cadoryx\Cadoryx.lnk'
$cadUserData=Join-Path $env:LOCALAPPDATA 'Cadoryx'
$cadHadData=Test-Path -LiteralPath $cadUserData
$cadInstalled=$false
function RunMsi([string[]]$arguments,[string]$log){
    $args=@($arguments)+@('/qn','/norestart','/L*v',('"'+$log+'"'))
    $process=Start-Process -FilePath msiexec.exe -ArgumentList $args -Wait -PassThru -WindowStyle Hidden
    if($process.ExitCode -ne 0){throw "Windows Installer exited $($process.ExitCode); see $log"}
}
try{
    RunMsi @('/i',('"'+$cadMsi+'"'),('INSTALLFOLDER="'+$cadInstall+'"')) (Join-Path $cadOutput 'install.log')
    $cadInstalled=$true
    if(!(Test-Path -LiteralPath $cadLink)){throw 'Start-menu shortcut was not installed.'}
    if(!@(GetCadoryxRegistration).Count){throw 'Windows uninstall registration is missing.'}
    $cadFiles=@($cadManifest.files)
    foreach($file in $cadFiles){
        $path=Join-Path $cadInstall $file.path
        if(!(Test-Path -LiteralPath $path -PathType Leaf)){throw "Installed file missing: $($file.path)"}
        $hash=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if($hash -ne $file.sha256){throw "Installed file differs: $($file.path)"}
    }
    $cadSmoke=Join-Path $cadOutput 'smoke'
    $exe=Join-Path $cadInstall 'Cadoryx.wpf.exe'
    $start=[Diagnostics.ProcessStartInfo]::new($exe)
    $start.WorkingDirectory=$cadInstall
    $start.UseShellExecute=$false
    $start.CreateNoWindow=$true
    $start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    if($PSVersionTable.PSVersion.Major -ge 7){
        $start.ArgumentList.Add('--smoke')
        $start.ArgumentList.Add($cadSmoke)
        $start.Environment['PATH']="$env:WINDIR\System32;$env:WINDIR"
        $null=$start.Environment.Remove('DOTNET_ROOT')
    }else{
        $start.Arguments='--smoke "'+$cadSmoke+'"'
        $start.EnvironmentVariables['PATH']="$env:WINDIR\System32;$env:WINDIR"
        $start.EnvironmentVariables.Remove('DOTNET_ROOT')
    }
    $process=[Diagnostics.Process]::Start($start)
    try{
        if(!$process.WaitForExit(180000)){
            if($PSVersionTable.PSVersion.Major -ge 7){$process.Kill($true)}else{$process.Kill()}
            throw 'Installed desktop smoke timed out.'
        }
        if($process.ExitCode -ne 0){throw "Installed desktop smoke exited $($process.ExitCode)"}
    }
    finally{$process.Dispose()}
    $result=Get-Content -LiteralPath (Join-Path $cadSmoke 'result.txt') -Raw
    if(!$result.StartsWith('PASS:')){throw 'Installed desktop smoke failed.'}
    if((Get-Item -LiteralPath (Join-Path $cadSmoke 'bindings.log')).Length -ne 0){
        throw 'Installed desktop smoke recorded WPF binding errors.'
    }
}
finally{
    if($cadInstalled){
        RunMsi @('/x',('"'+$cadMsi+'"')) (Join-Path $cadOutput 'uninstall.log')
        if(Test-Path -LiteralPath (Join-Path $cadInstall 'Cadoryx.wpf.exe')){
            throw 'Uninstall left the executable behind.'
        }
        if(Test-Path -LiteralPath $cadLink){throw 'Uninstall left the start-menu shortcut behind.'}
        if(@(GetCadoryxRegistration).Count){throw 'Uninstall registration remains.'}
        if($cadHadData -and !(Test-Path -LiteralPath $cadUserData)){
            throw 'Uninstall removed existing Cadoryx user data.'
        }
    }
}
Set-Content -LiteralPath (Join-Path $cadOutput 'result.txt') -Value "PASS: $($cadManifest.fileCount) installed file hashes, start-menu/uninstall registration, self-contained desktop smoke, zero binding errors, clean uninstall and preserved existing user data."
Write-Output "Evidence: $cadOutput"
