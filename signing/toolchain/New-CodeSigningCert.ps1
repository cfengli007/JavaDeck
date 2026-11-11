$ErrorActionPreference = 'Stop'
$pfxPath = Join-Path $PSScriptRoot 'JavaDeck-CodeSign.pfx'
$pfxPassword = ConvertTo-SecureString -String '123456' -Force -AsPlainText
$subject = 'CN=cf192 Code Signing'
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue | Where-Object { $_.Subject -eq $subject } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -FriendlyName 'cf192 Code Signing' -KeyExportPolicy Exportable -KeyAlgorithm RSA -KeyLength 3072 -NotAfter (Get-Date).AddYears(10) -CertStoreLocation Cert:\CurrentUser\My
    Write-Host ("CREATED " + $cert.Thumbprint)
} else {
    Write-Host ("REUSED " + $cert.Thumbprint)
}
if (-not (Test-Path -LiteralPath $pfxPath)) {
    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $pfxPassword
    Write-Host ("PFX_EXPORTED " + $pfxPath)
} else {
    Write-Host ("PFX_EXISTS " + $pfxPath)
}
$cerPath = Join-Path $PSScriptRoot 'JavaDeck-CodeSign.cer'
Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null
$tp = 'Cert:\CurrentUser\TrustedPublisher'
if (-not (Get-ChildItem $tp -ErrorAction SilentlyContinue | Where-Object { $_.Thumbprint -eq $cert.Thumbprint })) {
    Import-Certificate -FilePath $cerPath -CertStoreLocation $tp | Out-Null
    Write-Host 'TRUSTED_INSTALLED'
} else {
    Write-Host 'TRUSTED_ALREADY'
}
Write-Host ("THUMBPRINT=" + $cert.Thumbprint)
Write-Host ("NOT_AFTER=" + $cert.NotAfter)
