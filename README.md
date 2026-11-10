# JavaDeck

Windows 原生的 JDK 版本管理器，为「多版本 Minecraft + 其他 Java 软件」场景定制。

- **技术栈**：WPF / .NET 8
- **JDK 来源**：BellSoft Liberica（LTS 8 / 11 / 17 / 21 / 25）
- **体积**：应用本体约 250 KB（框架依赖，不打包 JRE）
- **许可**：GPL-3.0

## 功能

| 功能 | 说明 |
|---|---|
| 多版本共存 | 每个 JDK 解压到独立目录，互不干扰 |
| 扫描导入 | 自动发现 scoop / BellSoft / Adoptium / Corretto / IDEA `.jdks` / Gradle 里的 JDK |
| 静默安装 | 从 BellSoft 官方 API 拉版本 + 下载 + SHA1 校验 + 解压，全程无弹窗 |
| 变体支持 | 标准 / Full（含 JavaFX）/ Lite 三种构建，从文件名后缀精确识别 |
| 多线程下载 | HTTP Range 分段并行，默认 6 段，可调 |
| 安装队列 | 下载并行、解压串行，实时进度，可单独取消 |
| 环境变量管理 | 安装自动加入 PATH 末尾；设为 JAVA_HOME 时从 PATH 移除 |
| PATH 排序 | 只管理 JDK 之间的相对顺序，其它路径位置不动 |
| 更新检查 | 逐版本查询 BellSoft 的 `latestInFeatureVersion` 标志 |
| Minecraft 对照 | 拉 Mojang 版本清单，标出各 MC 版本需要的 Java |
| 深色主题 | 浅色 / 深色 / 跟随系统，含 DWM 原生深色框架 |
| 托盘常驻 | 关闭窗口最小化到托盘 |
| 开机自启 | 写 `HKCU\...\Run`，不需要管理员 |
| 代理支持 | http / socks5，可测试连通性 |
| 应用自更新 | 从 GitHub Releases 检查并下载新版本 |

## 安装

从 [Releases](https://github.com/cfengli007/JavaDeck/releases) 下载最新的 `JavaDeck-Setup-*.exe` 运行。

安装向导为标准 Inno Setup 界面（简体中文）：

```
欢迎 → 选择安装目录 → 附加任务（桌面快捷方式）→ 安装 → 完成
```

**前置条件**：.NET 8 桌面运行时。安装程序会检测并提示下载。

**卸载**：设置 → 应用 → JavaDeck，或运行安装目录下的 `unins000.exe`。

### 为什么用打过补丁的 Inno Setup

部分国产安全软件（如火绒引擎）会拦截「非提权进程静默写 `%TEMP%`」，报 Error 5。

Inno Setup 默认的 `SetupLdr.e32` 清单是 `asInvoker`，会先把文件解到 `%TEMP%` 再申请 UAC —— 正好撞上。

解决办法是把 `SetupLdr.e32` 的 manifest 改为 `requireAdministrator`，让 loader **启动即提权**，之后才创建临时目录。补丁脚本见 `installer/Patch-InnoSetup-Loader.ps1`。

## 命令行

```powershell
JavaDeck.exe scan                 # 扫描本机 JDK
JavaDeck.exe import               # 导入未登记的
JavaDeck.exe list                 # 列出已登记（* 表示当前使用）
JavaDeck.exe use 21               # 切换 JAVA_HOME
JavaDeck.exe env                  # 查看 JAVA_HOME / PATH / java -version
JavaDeck.exe install 17           # 下载安装最新 Java 17
JavaDeck.exe remove 11            # 删除某个版本

JavaDeck.exe --theme dark         # 强制深色主题
```

## 目录结构

```
%USERPROFILE%\Java\            （默认安装根目录，界面里可改）
  jdk-8u504.1
  jdk-11.0.33.0.3.7
  jdk-17.0.21.0.3.5
  jdk-21.0.13.0.3.5
  jdk-25.0.5.0.3.5
  jdk-index.json
```

## 构建

需要 .NET 8 SDK 和 Inno Setup 6。

```powershell
# 1. 首次需要把 Inno Setup 复制到 signing 目录并打补丁
#    （见 installer/Patch-InnoSetup-Loader.ps1）
Copy-Item "C:\Program Files (x86)\Inno Setup 6" .\signing\InnoSetup6-patched -Recurse
.\installer\Patch-InnoSetup-Loader.ps1 -InnoDir .\signing\InnoSetup6-patched

# 2. 打包
.\installer\Build-Installer.ps1
```

产物在 `installer\dist\JavaDeck-Setup-<version>-win-x64.exe`。

## 环境变量规则

```
安装 JDK     → 它的 bin 追加到用户 PATH 末尾
设为 JAVA_HOME → 从 PATH 移除（避免重复）
切换 JAVA_HOME → 旧版 bin 加回 PATH，新版移除
```

全部写用户级（`HKCU\Environment`），不需要管理员。

## 更新检查的 API 用法

JavaDeck 用 BellSoft 官方 API 的三个不同接口：

| 场景 | 接口 | 开销 |
|---|---|---|
| 检查某个已装版本是否最新 | `?version-feature=N&version=X` 读 `latestInFeatureVersion` | ~640 B |
| 拿某大版本最新构建 | `?version-modifier=latest` | ~1.2 KB |
| 添加 JDK 时的完整列表 | `?version-feature=N&bundle-type=...` | ~256 KB |

注意：`version-modifier=latest` 返回的是「该大版本的最新」，**不含「你装的是否最新」语义**，
所以检查更新必须用按 `version` 查询读 `latestInFeatureVersion`。

## 许可证

[GNU General Public License v3.0](LICENSE)

Copyright (C) 2026 JavaDeck Contributors

本程序是自由软件，你可以自由复制、修改和再分发，但不提供任何担保。
