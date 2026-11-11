param(
    [Parameter(Mandatory=$true)][string[]]$Path
)
$ErrorActionPreference = 'Stop'
$subject = 'CN=cf192 Code Signing'
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue | Where-Object { $_.Subject -eq $subject } | Select-Object -First 1
if (-not $cert) { throw '代码签名证书不存在，请先运行 New-CodeSigningCert.ps1' }
foreach ($f in $Path) {
    if (-not (Test-Path -LiteralPath $f)) { Write-Warning ("跳过不存在: " + $f); continue }
    Set-AuthenticodeSignature -FilePath $f -Certificate $cert
    $sig = Get-AuthenticodeSignature $f
    if ($sig.Status -ne 'Valid') { throw ("签名失败 " + $f + " 状态 " + $sig.Status) }
    Write-Host ("SIGNED " + $f + " -> " + $sig.Status)
}
