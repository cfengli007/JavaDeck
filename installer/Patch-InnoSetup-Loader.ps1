# 将 Inno Setup 6 的 SetupLdr.e32 的 manifest executionLevel 从 asInvoker 改为 requireAdministrator
# 目的：让安装器 loader 启动即弹 UAC（先申请），再创建临时目录（后创建），
#       绕过联想内置火绒引擎对"非提权进程静默写 Temp"的拦截。
# 用法：在管理员 PowerShell 中运行（需写 C:\Program Files (x86)\Inno Setup 6\SetupLdr.e32）。
param(
    [string]$InnoDir = (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6")
)

$ErrorActionPreference = "Stop"

# 定位 mt.exe（Windows SDK，优先 x64）
$mt = $null
$kitRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
if (Test-Path $kitRoot) {
    $mt = Get-ChildItem -Path $kitRoot -Recurse -Filter mt.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\x64\\" } |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $mt) { throw "未找到 mt.exe，请安装 Windows SDK。" }
Write-Host ("mt.exe = " + $mt)

$ldr = Join-Path $InnoDir "SetupLdr.e32"
if (-not (Test-Path -LiteralPath $ldr)) { throw ("SetupLdr.e32 不存在: " + $ldr) }

$work = Join-Path $InnoDir "_patchwork"
if (-not (Test-Path $work)) { New-Item -ItemType Directory -Path $work | Out-Null }
$mf  = Join-Path $work "setupldr.manifest"
$mfe = Join-Path $work "setupldr.elevated.manifest"
$mfv = Join-Path $work "verify.manifest"

# 1. 提取
$arg1 = "-inputresource:$ldr;#1"
$arg2 = "-out:$mf"
$p = Start-Process -FilePath $mt -ArgumentList $arg1, $arg2 -Wait -PassThru -NoNewWindow
if ($p.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $mf)) { throw "提取 manifest 失败" }
$m = Get-Content -Raw -LiteralPath $mf
$levelBefore = [regex]::Match($m, "level=`"([^`"]+)`"").Groups[1].Value
Write-Host ("提取成功，当前 level = " + $levelBefore)

if ($levelBefore -eq "requireAdministrator") {
    Write-Host "已经是 requireAdministrator，无需修改。"
    Remove-Item -LiteralPath $mf -Force -ErrorAction SilentlyContinue
    exit 0
}

# 2. 替换
$m2 = $m.Replace("level=`"asInvoker`"", "level=`"requireAdministrator`"")
Set-Content -LiteralPath $mfe -Value $m2 -NoNewline -Encoding UTF8

# 3. 嵌入
$arg3 = "-manifest:$mfe"
$arg4 = "-outputresource:$ldr;#1"
$p2 = Start-Process -FilePath $mt -ArgumentList $arg3, $arg4 -Wait -PassThru -NoNewWindow
if ($p2.ExitCode -ne 0) { throw "嵌入 manifest 失败" }

# 4. 回读验证
$p3 = Start-Process -FilePath $mt -ArgumentList $arg1, $arg2 -Wait -PassThru -NoNewWindow
$mv = Get-Content -Raw -LiteralPath $mf
$levelAfter = [regex]::Match($mv, "level=`"([^`"]+)`"").Groups[1].Value
Write-Host ("修改完成，新 level = " + $levelAfter)

# 清理
Remove-Item -LiteralPath $mf, $mfe, $mfv -Force -ErrorAction SilentlyContinue
if ($levelAfter -ne "requireAdministrator") { throw "验证失败" }
Write-Host "PATCH OK"
