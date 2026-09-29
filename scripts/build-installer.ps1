param(
    [string]$Version='0.4.12',
    [string]$Output,
    [string]$SigningThumbprint
)
$ErrorActionPreference='Stop'
$cadRoot=Split-Path -Parent $PSScriptRoot
$cadOutput=if($Output){[IO.Path]::GetFullPath($Output)}else{
    Join-Path $cadRoot ('artifacts/installer-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
}
if(Test-Path -LiteralPath $cadOutput){throw 'Choose a new output directory to preserve prior evidence.'}
if($Version -notmatch '^\d+\.\d+\.\d+$'){throw 'MSI version must have three numeric parts.'}
New-Item -ItemType Directory -Path $cadOutput -Force | Out-Null
$cadPublish=Join-Path $cadOutput 'publish'
$cadWxs=Join-Path $cadOutput 'Files.wxs'
$cadMsi=Join-Path $cadOutput "Cadoryx-$Version-win-x64.msi"
Push-Location $cadRoot
try{
    dotnet tool restore
    if($LASTEXITCODE -ne 0){throw 'WiX tool restore failed.'}
    dotnet restore Cadoryx.wpf/Cadoryx.wpf.csproj --configfile (Join-Path $cadRoot 'NuGet.Config') --locked-mode -p:SelfContained=true --nologo -v minimal
    if($LASTEXITCODE -ne 0){throw 'Locked self-contained restore failed.'}
    dotnet publish Cadoryx.wpf/Cadoryx.wpf.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishTrimmed=false -o $cadPublish --nologo -v minimal
    if($LASTEXITCODE -ne 0){throw 'Self-contained publish failed.'}
    foreach($relative in @('Cadoryx.wpf.exe','LICENSE','occt/OcctSharp.Native.dll','licenses/OcctSharp.Native/THIRD_PARTY_NOTICES.md','licenses/MathNet.Numerics/LICENSE.md')){
        if(!(Test-Path -LiteralPath (Join-Path $cadPublish $relative))){throw "Missing publish payload: $relative"}
    }
    $cadBaseline=Get-Content -LiteralPath (Join-Path $cadPublish 'runtime-baseline.json') -Raw | ConvertFrom-Json
    $cadPackage=([xml](Get-Content -LiteralPath 'Directory.Build.props' -Raw)).Project.PropertyGroup.OcctSharpVersion
    $cadNativeHash=(Get-FileHash -LiteralPath (Join-Path $cadPublish 'occt/OcctSharp.Native.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
    if($cadBaseline.documentWriterVersion -ne $Version -or $cadBaseline.occtSharpPackage -ne $cadPackage -or
       $cadBaseline.nativeSha256 -ne $cadNativeHash){throw 'Runtime baseline does not match the installer version or locked native package.'}
    foreach($relative in @('hostfxr.dll','hostpolicy.dll')){
        if(!(Test-Path -LiteralPath (Join-Path $cadPublish $relative))){throw "Self-contained runtime is missing $relative"}
    }
    $cadFiles=@(Get-ChildItem -LiteralPath $cadPublish -File -Recurse | Sort-Object FullName)
    function StableId([string]$prefix,[string]$path){
        $bytes=[Text.Encoding]::UTF8.GetBytes($path.ToLowerInvariant().Replace('\','/'))
        return $prefix+[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).Substring(0,16)
    }
    $cadXml=[Xml.XmlDocument]::new()
    $cadXml.AppendChild($cadXml.CreateXmlDeclaration('1.0','utf-8',$null)) | Out-Null
    $cadNs='http://wixtoolset.org/schemas/v4/wxs'
    function AddElement([Xml.XmlNode]$parent,[string]$name,[hashtable]$attributes){
        $node=$cadXml.CreateElement($name,$cadNs)
        foreach($key in $attributes.Keys){$node.SetAttribute($key,[string]$attributes[$key])}
        $parent.AppendChild($node) | Out-Null
        return $node
    }
    $root=$cadXml.CreateElement('Wix',$cadNs)
    $cadXml.AppendChild($root) | Out-Null
    $fragment=AddElement $root 'Fragment' @{}
    $directoryRef=AddElement $fragment 'DirectoryRef' @{Id='INSTALLFOLDER'}
    $groupFragment=AddElement $root 'Fragment' @{}
    $group=AddElement $groupFragment 'ComponentGroup' @{Id='ApplicationFiles'}
    $directories=@{}
    foreach($file in $cadFiles){
        $relative=[IO.Path]::GetRelativePath($cadPublish,$file.FullName)
        $parts=$relative.Split([IO.Path]::DirectorySeparatorChar)
        $parent=$directoryRef
        $key=''
        for($i=0;$i -lt $parts.Length-1;$i++){
            $key=if($key){$key+'\'+$parts[$i]}else{$parts[$i]}
            if(!$directories.ContainsKey($key)){
                $directories[$key]=AddElement $parent 'Directory' @{Id=(StableId 'D' $key);Name=$parts[$i]}
            }
            $parent=$directories[$key]
        }
        $path=$relative.Replace('\','/')
        $componentId=StableId 'C' $path
        $component=AddElement $parent 'Component' @{Id=$componentId;Guid='*'}
        $null=AddElement $component 'File' @{Id=(StableId 'F' $path);Source=$file.FullName;KeyPath='yes'}
        $null=AddElement $group 'ComponentRef' @{Id=$componentId}
    }
    $cadXml.Save($cadWxs)
    dotnet wix build installer/Cadoryx.wxs $cadWxs -arch x64 -d "ProductVersion=$Version" -out $cadMsi -pdbtype none
    if($LASTEXITCODE -ne 0){throw 'WiX MSI build failed.'}
    $cadSigner=$null
    if($SigningThumbprint){
        $cadSigner=Get-Item -LiteralPath ("Cert:\CurrentUser\My\"+$SigningThumbprint) -ErrorAction Stop
        if(!$cadSigner.HasPrivateKey -or !@($cadSigner.EnhancedKeyUsageList | Where-Object ObjectId -eq '1.3.6.1.5.5.7.3.3').Count){
            throw 'The selected certificate needs a private key and code-signing usage.'
        }
        $cadSignTool=Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Filter signtool.exe -Recurse |
            Where-Object { $_.FullName -match '\\x64\\signtool.exe$' } | Sort-Object FullName -Descending | Select-Object -First 1
        if(!$cadSignTool){throw 'Windows SDK x64 signtool.exe is unavailable.'}
        & $cadSignTool.FullName sign /sha1 $cadSigner.Thumbprint /s My /fd SHA256 $cadMsi
        if($LASTEXITCODE -ne 0){throw 'MSI test signing failed.'}
        $cadSignature=Get-AuthenticodeSignature -LiteralPath $cadMsi
        if($cadSignature.SignerCertificate.Thumbprint -ne $cadSigner.Thumbprint){throw 'MSI signer does not match the requested certificate.'}
    }
    $cadManifest=@($cadFiles | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($cadPublish,$_.FullName).Replace('\','/')
            bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    [ordered]@{
        schemaVersion=1;product='Cadoryx';version=$Version;runtime='win-x64';selfContained=$true
        occtSharpVersion=$cadPackage
        signingThumbprint=if($cadSigner){$cadSigner.Thumbprint}else{$null}
        fileCount=$cadFiles.Count;files=$cadManifest
        msiSha256=(Get-FileHash -LiteralPath $cadMsi -Algorithm SHA256).Hash.ToLowerInvariant()
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $cadOutput 'manifest.json') -Encoding utf8
    Write-Output "Installer: $cadMsi"
    Write-Output "Manifest: $(Join-Path $cadOutput 'manifest.json')"
}
finally{Pop-Location}
