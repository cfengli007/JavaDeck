# 开发经验总结

记录 JavaDeck 从零到发布过程中踩过的坑。按主题归类，每条都对应一个实际发生的问题。

---

## 一、WPF 框架相关

### 1. 启动时在 UI 线程做磁盘 IO = 白屏假死

**症状**：双击打开，白屏几秒，然后闪退，或者"过一会儿能点但立刻又卡"。

**原因**：`MainWindow` 构造后立刻在 UI 线程递归扫描 `C:\`、`D:\` 找 JDK，耗时 **37 秒**。

**解决**：
- 所有 IO / 网络移到 `Task.Run`
- 扫描限定固定路径 + 深度上限（`JdkScanner`），**37 秒 → 51 毫秒**
- 子进程探测加超时（`ProbeJavaVersion` 的 `WaitForExit(5000)`）

**教训**：UI 线程上任何超过 100ms 的同步操作都要挪走。不要相信"这个目录应该很快"。

### 2. `InvariantGlobalization=true` 会让 WPF 数据绑定崩溃

**症状**：点 CheckBox 就卡住，或者抛 `InvalidOperationException: Cannot find non-neutral culture related to 'en-us'`。

**原因**：为减小体积加了 `<InvariantGlobalization>true</InvariantGlobalization>`，WPF 绑定在 `ToggleButton.OnToggle` 里尝试解析 `en-us` 区域性，失败。

**解决**：去掉该开关，并在 `App.OnStartup` 最先固定区域性：

```csharp
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
```

**教训**：WPF 依赖完整的全球化数据，不要为了几 KB 砍掉它。

### 3. `DynamicResource` 换主题不生效的三种情况

这是本次花时间最多的问题。全部踩过：

**① 相对 URI 找不到资源**
```csharp
// 错：运行时被解析成 "themes/dark.xaml"，PackWebRequest 找不到
new Uri("Themes/Dark.xaml", UriKind.Relative)
// 对：绝对 pack URI
new Uri("pack://application:,,,/JavaDeck;component/Themes/Dark.xaml", UriKind.Absolute)
```

**② 替换字典实例不触发重绘**
```csharp
// 错：MergedDictionaries[0] = new ResourceDictionary{...}
//     资源值变了但已绑定元素不重新求值
// 对：操作同一实例
target.Source = newUri;
```

**③ `Show()` 时部分属性被重置**

调试探针发现的：
```
窗口创建后（未 Show）Background = #FF15171C   ← 对的
Show 后              Background = #FFF3F4F7   ← 被重置
```
根因是 `Loaded` 事件里还有一段代码从配置文件读主题，覆盖了命令行传入的。

**④ 子窗口不跟随主题**

只设 `Window.Background` 不够，子窗口内部的 `Border`/`Grid` 用的 `DynamicResource` 在 `Show()` 那一刻已求值。
解决：遍历可视化树，把属于"已知主题背景色"的元素重新推成当前主题色（`ThemeManager.ReassertBrushes`）。

**教训**：
- 主题系统要用 `DynamicResource` 而非 `StaticResource`
- 用 `EventManager.RegisterClassHandler(typeof(Window), LoadedEvent, ...)` 做全局钩子，避免逐个窗口改
- 调试时写探针文件比猜快得多

### 4. `WindowStyle=None` 的三个副作用

| 问题 | 解决 |
|---|---|
| 窗口是直角，没有圆角 | `DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE(33), Round)` |
| 标题栏按钮点不动 | `WindowChrome.IsHitTestVisibleInChrome="True"` + 拖动逻辑要排除按钮区域 |
| 最大化盖住任务栏 | 用 `WindowChrome` 而非手工 `WindowState` |

**标题栏按钮点不动的真实原因**（排查了 3 层才找到）：

```
WM_NCHITTEST 返回 HTCLIENT(1)      ← 系统层没问题
IsHitTestVisibleInChrome = True    ← 命中测试没问题
AutomationPeer.Invoke() 能触发      ← 逻辑没问题
```

最后发现是拖动处理器的问题：
```csharp
private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
{
    if (e.ButtonState == MouseButtonState.Pressed) DragMove();  // ← 元凶
}
```
点按钮时事件冒泡到标题栏 Grid，`DragMove()` 抢走鼠标捕获进入模态循环，按钮的 `Click` 永远等不到 `MouseUp`。

修法：
```csharp
if (IsInButton(e.OriginalSource as DependencyObject)) return;
```

### 5. 自定义 `ContextMenu` 模板的两个坑

```xml
<!-- 坑 1：没有 PlacementTarget，子菜单不显示 -->
<Popup x:Name="PART_Popup" Placement="Right" IsOpen="{TemplateBinding IsSubmenuOpen}">
<!-- 对 -->
<Popup x:Name="PART_Popup" Placement="Right" PlacementTarget="{Binding ElementName=b}" ...>
```

坑 2：`IsCheckable` 是逻辑属性，自定义模板不画就对勾不可见。要加 `Trigger Property="IsChecked"` 显示 `Path`。

坑 3：`IsOpen = true` 而不设 `PlacementTarget`，菜单位置算成屏幕左上角。

---

## 二、Windows 系统集成

### 1. SChannel 在受限环境下的失败

```
schannel: AcquireCredentialsHandle failed: SEC_E_NO_CREDENTIALS
The SSL connection could not be established
```

出现场景：
- DSH 沙箱内 .NET `HttpClient` 全部失败
- 但 Node 的 fetch（OpenSSL）正常
- `gh api` 正常（走 Go 的 TLS 实现）

**结论**：SChannel 需要访问命名管道与 LSASS 通信，被沙箱禁止。真实桌面环境无此问题。

**兜底方案**：命令行工具（`gh`、`curl`）通常自带 TLS 实现，可作为备用通道。

### 2. 中文 Windows 控制台编码

PowerShell 输出中文到管道时可能乱码。需要：
```powershell
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
chcp 65001
```

### 3. 注册表路径的 WOW64 重定向

查 .NET 运行时，按 64 位视图读 `SOFTWARE\dotnet\...` 找不到，实际登记在 `WOW6432Node`：

```csharp
// 错：只读一个视图
RegistryKey.OpenBaseKey(LocalMachine, RegistryView.Registry64)
// 对：两个都查
foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
```

更可靠的判断是查**目录**：`C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8.*`

### 4. junction 的坑

```csharp
// 目录联接的 reparse point 需要 NT 对象管理器前缀
var substitute = @"\??\" + path;   // 不是 \\
```

另外：`Directory.Delete(path, true)` 删不掉 junction，要用 `cmd /c rmdir`。

---

## 三、打包与分发

### 1. Inno Setup 的提权时序

**核心问题**：Inno 默认的 `SetupLdr.e32` 清单是 `asInvoker`，会先把文件解到 `%TEMP%` **再**申请 UAC。国产安全软件（联想电脑管家内置的火绒引擎）会拦截"非提权进程静默写 Temp"，报 Error 5。

**解决方案**：把 `SetupLdr.e32` 的 manifest 改成 `requireAdministrator`，让 loader **启动即提权**。

```powershell
# 用 mt.exe 提取/替换内嵌 manifest
mt.exe -inputresource:SetupLdr.e32;#1 -out:ldr.manifest
# 改 level="asInvoker" -> "requireAdministrator"
mt.exe -manifest:ldr.elevated.manifest -outputresource:SetupLdr.e32;#1
```

验证：
```powershell
$b = [IO.File]::ReadAllBytes($exe)
$a = [Text.Encoding]::ASCII.GetString($b)
$i = $a.IndexOf('requestedExecutionLevel')
$a.Substring($i, 50).Split([char]0)[0]
```

### 2. 不要自己写安装器

**曾经的错误尝试**：手写一个 WinForms 安装器（`requireAdministrator` 清单 + 自解压 zip）。

结果："和整个系统格格不入"——因为 WinForms 默认样式和 Windows 安装向导完全不同。

**正确做法**：用 Inno Setup 的标准向导，只打补丁解决提权时序问题（见上一条）。

### 3. 单文件发布的 ILLink 陷阱

想用 `-p:PublishSingleFile=true` 打单文件时，即使 `PublishTrimmed=false` 也会要求 `Microsoft.NET.ILLink.Tasks` 包。
离线环境（NuGet 缓存里没有）会直接失败：

```
error NU1100: 无法解析 net8.0-windows 的 Microsoft.NET.ILLink.Tasks (>= 8.0.20)
```

**替代方案**：
- 框架依赖多文件（`bin\Release\net8.0-windows\` 下 4-5 个文件），由 Inno 打包成单个 exe
- 安装包体积约 2 MB（.NET 运行时用户机器已有）

### 4. 安装包的版本号要跟 csproj 同步

Inno 脚本里的 `#define AppVersion` 要能从命令行覆盖，否则每次发布都要手改：

```iss
#ifndef AppVersion
  #define AppVersion "2.5.0"
#endif
```
```powershell
ISCC.exe /DAppVersion=2.5.0 JavaDeckSetup.iss
```

### 5. 旧版本残留的卸载项

早期方案（自建 WinForms 安装器）留下的 `HKLM\...\Uninstall\JavaDeck` 不会自动清除。
换用 Inno 后会变成 `{GUID}_is1`，两套并存：

```
JavaDeck                                  ← 旧的，1.1.0
{8F3C2A41-...}_is1                        ← Inno，2.5.0
```

需要在管理员 PowerShell 里手动删旧项。

---

## 四、GitHub 集成

### 1. REST API 的未认证限流

```
403 API rate limit exceeded for <IP>
```
限额：**60 次/小时/IP**。做自更新检查很容易超。

**替代方案**（都不限流）：

| 用途 | 端点 | 返回 |
|---|---|---|
| 最新版本号 | `GET /releases/latest` | 302，读 `Location` 里的 tag |
| 资产文件名 | `GET /releases/expanded_assets/<tag>` | HTML 片段，正则提取 |
| 更新说明 | `GET /releases.atom` | Atom XML |

实测连续 8 次：API `403×8`，上述三个 `302×8` / `200×8`。

### 2. `/releases/latest` 有边缘缓存

刚发布的新版本可能取不到。**必须加 cache-buster**：
```csharp
$"{BaseUrl}/releases/latest?_={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
```
同时用 `atom`（实时）做交叉校验。

### 3. 沙箱内 git push 失败

```
fatal: unable to access '...': schannel: AcquireCredentialsHandle failed
```
改用 token 内联：
```powershell
git remote set-url origin "https://x-access-token:$token@github.com/owner/repo.git"
git push origin main
git remote set-url origin 'https://github.com/owner/repo.git'   # 立刻清掉 token
```

---

## 五、约定与排序

### 1. 版本号比较不能简单拼接数字

`21.0.13` vs `21.0.12.1`，直接拼接会得到错误结果。

正确做法：**每段固定 4 位十进制**：
```csharp
key = key * 10000 + segment;   // 21.0.13 -> 21000130000
                               // 21.0.12.1 -> 21000120001
```

### 2. BellSoft API 的变体是独立 bundle-type

不是文件名后缀（虽然后缀确实也不同）。要分三次查：
```
bundle-type=jdk        -> standard
bundle-type=jdk-full   -> full（含 JavaFX）
bundle-type=jdk-lite   -> lite
```

### 3. `latestInFeatureVersion` ≠ `latest`

- `latest=true`：全平台最新
- `latestInFeatureVersion=true`：该大版本最新

检查"我装的是否最新"必须用后者。实测 Java 25：`version-modifier=latest` 返回 25.0.4.1，但实际最新是 25.0.5.0.3+5。

---

## 六、工程实践

### 1. 先探测环境，再定方案

本项目开工前先跑了诊断脚本，得到的关键事实直接决定了架构：

- \`.NET 8 桌面运行时已装\` → 可以做框架依赖的单文件
- \`已有 PCL + .minecraft\` → 需要 Minecraft 版本对照
- \`scoop 里 5 个 full 版 JDK 共 2.8 GB\` → 需要变体识别 + 全量替换
- \`没有 JAVA_HOME 但有 5 条 PATH\` → 需要 PATH 清理功能

### 2. 用探针代替猜测

排查深色主题问题时，写了个探针把实际值 dump 到文件：
```csharp
File.WriteAllText(@"D:\...\theme-debug.txt",
    $"args={args} theme={theme} dark={isDark} bg={bgColor}");
```
一眼看出 `theme=Dark dark=True bg=#FF15171C` 但窗口仍是浅色 —— 定位到"资源对但没传播"。

### 3. 自动化测试要能真正验证

写过的有效测试：
- **UI 命中测试**：`WM_NCHITTEST` 返回值判断按钮区域是否被系统拦截
- **窗口主题验证**：反射创建窗口，读 `Background` 颜色
- **字节级清单检查**：读 exe 字节找 `requestedExecutionLevel`
- **网络端点实测**：Node fetch 测 8 次看限流

无效的测试：
- `PrintWindow` 截图 —— 返回缓存表面，三次结果字节完全相同才发现

### 4. 沙箱限制要如实告诉用户

反复出现的模式：
```
我这边沙箱写不了 C:\Users\...\AppData
我这边 SChannel 连不上 HTTPS
我这边 UAC 弹窗没人点
```
这些不是产品 bug，要明确区分并给出用户侧验证方法。