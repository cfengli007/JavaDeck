$ErrorActionPreference = 'Stop'
$cerPath = Join-Path $PSScriptRoot 'JavaDeck-CodeSign.cer'
$subject = 'CN=cf192 Code Signing'
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -eq $subject } | Select-Object -First 1
Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null
$root = 'Cert:\CurrentUser\Root'
if (-not (Get-ChildItem $root -ErrorAction SilentlyContinue | Where-Object { $_.Thumbprint -eq $cert.Thumbprint })) {
    Import-Certificate -FilePath $cerPath -CertStoreLocation $root | Out-Null
    Write-Host 'ROOT_INSTALLED'
} else {
    Write-Host 'ROOT_ALREADY'
}
