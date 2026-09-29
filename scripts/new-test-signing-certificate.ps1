$ErrorActionPreference='Stop'
$cadExisting=@(Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Subject -eq 'CN=yoiri' -and $_.NotAfter -gt (Get-Date).AddMonths(1) -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending)
$cadCertificate=if($cadExisting.Count){$cadExisting[0]}else{
    New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=yoiri' -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyExportPolicy NonExportable -KeyLength 3072 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(2)
}
Write-Output "Test code-signing certificate: $($cadCertificate.Thumbprint)"
Write-Output "Subject: $($cadCertificate.Subject)"
Write-Output "Expires: $($cadCertificate.NotAfter.ToString('u'))"
Write-Output 'This self-signed certificate is for local/VM verification; Windows does not trust it for public distribution.'
