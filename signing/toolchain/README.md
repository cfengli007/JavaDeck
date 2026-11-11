# 代码签名工具链

自签名代码签名证书与辅助脚本。用于给 `JavaDeck.exe` 和安装包签名，
减少 Windows SmartScreen / 国产安全软件对未签名程序的拦截。

## 内容

| 文件 | 用途 |
|---|---|
| `JavaDeck-CodeSign.pfx` | 自签名证书（含私钥），**不要提交到 Git** |
| `JavaDeck-CodeSign.cer` | 公钥证书，用于导入"受信任的根证书颁发机构" |
| `New-CodeSigningCert.ps1` | 重新生成证书（原证书过期或被吊销时） |
| `Sign-Files.ps1` | 给指定的 exe 签名 |
| `Trust-Root.ps1` | 把证书导入本机受信任根（**需要管理员**） |
| `SetupLdr.e32.*.manifest` | Inno Setup loader 补丁用的 manifest 变体 |
| `elevated.manifest`, `extracted.manifest`, `verify.manifest` | 调试用的中间产物 |

## 用法

```powershell
# 1. 首次：生成证书（已存在可跳过）
.\New-CodeSigningCert.ps1

# 2. 把证书导入受信任根（需管理员，否则签名仍会提示未知发布者）
Start-Process powershell -Verb RunAs -ArgumentList '-File', "$PWD\Trust-Root.ps1"

# 3. 给程序签名
.\Sign-Files.ps1 -Path ..\..\installer\dist\JavaDeck-Setup-2.5.0-win-x64.exe
```

## 注意

- **自签名证书不会被其他机器信任**，只对本机有效。要分发给别人，
  需要购买正式代码签名证书（约 100-400 美元/年）或使用 SignPath 等免费开源方案。
- `.pfx` 含私钥，已加入 `.gitignore`。如果泄露，任何人都能以你的名义签名。
- 证书有效期通常 1 年，过期后需重新生成并重新导入信任。
