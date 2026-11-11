# UI 设计经验总结

记录 JavaDeck 界面从深色到浅色再到双主题的演进，以及踩过的视觉问题。

---

## 一、最重要的一条：深色主题必须全栈一致

**最初的错误**：只给 `TextBlock` 设了亮色，其它控件用系统默认样式。

结果：深色背景 + **白底黑字的 ComboBox、右键菜单、CheckBox、ToolTip**，非常刺眼。

**根因**：这些控件的默认 `ControlTemplate` 硬编码了系统颜色，光设 `Foreground` 没用，必须**整套重写模板**。

实际重写的控件：

| 控件 | 为什么必须重写 |
|---|---|
| `ComboBox` | 下拉按钮和弹出层都是系统白色 |
| `ComboBoxItem` | 弹出项背景白色 |
| `ContextMenu` / `MenuItem` | 整个菜单白色 |
| `CheckBox` / `RadioButton` | 勾选框是系统控件 |
| `ToolTip` | 白色气泡 |
| `ScrollBar` | 系统滚动条在深色下很突兀 |
| `ProgressBar` | `PART_Indicator` 需要自己画 |

**教训**：WPF 没有"全局深色模式"。要么用第三方主题库（如 WPF-UI），要么每个控件模板都自己写。选后者的话，一开始就要把所有交互控件列全。

---

## 二、配色体系

### 1. 双主题用 ResourceDictionary 分离

```
Themes/
  Light.xaml   32 个颜色键
  Dark.xaml    同样的 32 个键
```

键的命名按**语义**而非**颜色**：

```xml
<!-- 对：语义化 -->
<SolidColorBrush x:Key="Panel" Color="#FFFFFF"/>
<SolidColorBrush x:Key="FgDim" Color="#5B6273"/>

<!-- 错：按颜色命名，换主题时名字就错了 -->
<SolidColorBrush x:Key="White" Color="#FFFFFF"/>
```

### 2. 实际用的色板

**浅色**：
```
Bg         #F3F4F7   窗口背景
Panel     #FFFFFF   卡片/面板
Card      #FAFBFC   列表项
Line      #E2E5EB   分隔线
Fg        #1C1F26   主文字
FgDim     #5B6273   次要文字
FgFaint   #9AA0AE   辅助文字
Accent    #2F6FE0   主色
```

**深色**：
```
Bg         #15171C
Panel     #1B1E25
Card      #21252E
Line      #2E333E
Fg        #E8EBF0
FgDim     #9BA3B4
FgFaint   #6A7285
Accent    #5B95F5   ← 深色下主色要提亮，否则对比度不够
```

### 3. 深色下的对比度陷阱

浅色主题的 `#2F6FE0` 蓝在深色背景上偏暗。深色主题里要提到 `#5B95F5`。

同理状态色：
```
Ok     #1E8E5A → #4FC98A
Warn   #B26A00 → #E8A93D
Danger #C62828 → #E8736B
```

---

## 三、布局

### 1. 从"右侧栏"到"顶部菜单"的演进

**初版**：左右两栏，右侧放"当前环境 / Minecraft 对照 / 启动器"。

**问题**：右侧信息量大但都是次要信息，占了 1/3 宽度，主列表被挤压。

**改版**：全部收到顶部菜单（文件/JDK/环境/更新/工具），主区域只留列表。

**教训**：**固定的次要信息不该占固定宽度**。能收进菜单就收进菜单，需要时再弹窗。

### 2. 三区结构

```
┌──────────────────────────────────┐
│ 标题栏 38px（自绘，可拖动）       │
├──────────────────────────────────┤
│ 菜单栏 34px                      │
├──────────────────────────────────┤
│                                  │
│ 内容区（*，可伸缩）              │
│                                  │
├──────────────────────────────────┤
│ 状态条（Auto，单行提示）         │
├──────────────────────────────────┤
│ 按钮栏 62px（主要操作）          │
└──────────────────────────────────┘
```

弹窗统一用这个结构，只把按钮栏从"主操作"换成"确定/取消"。

### 3. 列表行的信息密度

```xml
[Java 21]  21.0.13.0.3+5  [标准]              [设为 JAVA_HOME] [⋯]
           C:\Users\...\jdk-21.0.13.0.3.5
```

三列：
- 左：版本号徽标（固定 74px）
- 中：版本 + 变体徽标 + 路径（*，路径超长省略）
- 右：操作按钮（Auto）

**关键**：路径用 `TextTrimming="CharacterEllipsis"`，不要让长路径把布局撑破。

---

## 四、反馈与状态

### 1. 所有耗时操作都要有可见反馈

**踩过的坑**：设置 JAVA_HOME 会卡 1 秒（写注册表 + 广播 `WM_SETTINGCHANGE`），用户以为程序死了。

**解决**：
- `BusyOverlay`：半透明遮罩 + 旋转圆环
- `WM_SETTINGCHANGE` 广播改**线程池异步**，超时从 3s 降到 1s
- `java -version` 探测从保存路径移除，改主界面异步做

**教训**：同步写环境变量 + 广播 = 必然卡顿。要么异步广播，要么给转圈。

### 2. 转圈遮罩的坑

第一版 `BusyOverlay` 把整个 `Window.Content` 包进 Grid，**盖住了标题栏**，关闭按钮点不动。

修复：
- 遮罩只覆盖内容区（传 `Panel` 而不是 `Window`）
- 绝不设 `owner.IsEnabled = false`（那会让整个窗口包括标题栏失效）

### 3. 加载状态要分级

| 场景 | 反馈形式 |
|---|---|
| 启动读本地 | 全屏遮罩「正在启动…」 |
| 拉取网络列表 | 列表区局部遮罩 + 进度文字 |
| 下载安装包 | 进度条 + MB 计数 |
| 切换 JAVA_HOME | 遮罩「正在切换到 Java 21…」 |
| 检查更新 | 状态卡片文字变化 |

### 4. 错误提示必须可复制

系统 `MessageBox` 没法换主题，也不能复制堆栈。

自制的 `MsgWindow`：
- 深色/浅色跟随主题
- 「复制全部」按钮（消息 + 堆栈 + 日志路径）
- 标题栏左侧色条区分信息/警告/错误

---

## 五、交互细节

### 1. 多选列表不要依赖 Ctrl

```xml
<!-- 错：要按住 Ctrl 才能多选 -->
<ListBox SelectionMode="Extended">
<!-- 对：点一下就选中，再点取消 -->
<ListBox SelectionMode="Multiple">
```

### 2. 单选必须设 GroupName

```csharp
// 动态创建的 RadioButton，不设 GroupName 就不互斥
new RadioButton { GroupName = "JavaHomeChoice", ... }
```

前端"圈点亮但其它不消失"，就是这个原因。

### 3. 菜单项禁用状态要符合直觉

**踩过的坑**：`MenuInterval.IsEnabled = _updateSettings.Enabled`

用户关掉了"自动检查更新"，结果"检查频率"整个子菜单灰了。但逻辑上应该允许先设频率再开启。

**教训**：禁用父子联动要慎重，只在"操作真的无意义"时才禁用。

### 4. 状态用颜色 + 文字双编码

```
[使用中]  绿色底 + 文字     ← 不只用颜色
[可更新]  橙色底 + 箭头
[标准]    灰蓝底
```

色盲用户依赖文字，正常用户依赖颜色。

---

## 六、自绘标题栏规范

```xml
<Window WindowStyle="None" ResizeMode="CanResize">
  <shell:WindowChrome.WindowChrome>
    <shell:WindowChrome CaptionHeight="38" ResizeBorderThickness="6"
                        GlassFrameThickness="0" CornerRadius="0"
                        UseAeroCaptionButtons="False"/>
  </shell:WindowChrome.WindowChrome>

  <Grid>
    <Grid.RowDefinitions>
      <RowDefinition Height="38"/>
      ...
    </Grid.RowDefinitions>

    <!-- 标题栏 -->
    <Grid Grid.Row="0" Background="{DynamicResource TitleBarBg}"
          MouseLeftButtonDown="OnTitleBarDrag">
      <TextBlock Text="JavaDeck" Margin="14,0,0,0" VerticalAlignment="Center"/>

      <!-- 关键：按钮要脱离 chrome 命中区域 -->
      <StackPanel Orientation="Horizontal" HorizontalAlignment="Right"
                  shell:WindowChrome.IsHitTestVisibleInChrome="True">
        <Button Style="{StaticResource CaptionButton}" Click="OnMinimize">...</Button>
        <Button Style="{StaticResource CaptionButton}" Click="OnMaximize">...</Button>
        <Button Style="{StaticResource CloseButton}" Click="OnClose">...</Button>
      </StackPanel>
    </Grid>
  </Grid>
</Window>
```

配套的窗体验证：
```csharp
// 圆角（Windows 11）
DwmSetWindowAttribute(hwnd, 33 /*CORNER_PREFERENCE*/, ref round, 4);

// 深色框架
DwmSetWindowAttribute(hwnd, 20 /*USE_IMMERSIVE_DARK_MODE*/, ref dark, 4);

// 边框颜色
DwmSetWindowAttribute(hwnd, 34 /*BORDER_COLOR*/, ref colorRef, 4);
```

**大小规范**：
- 标题栏高度 40px（设置/关于/更新窗口）
- 关闭按钮 44×38，鼠标悬停变红 `#C42B1C`
- 其它按钮悬停 `{DynamicResource CaptionHover}`

---

## 七、验证方法

### 视觉验证的坑

`PrintWindow` API 截窗口**不可靠**——它返回的是缓存的表面。三次截图字节完全相同才发现问题。

可靠的验证方式：

1. **读控件属性**（最准）
```csharp
Console.WriteLine(win.Background);        // #FF15171C
Console.WriteLine(border.Background);     // 子元素也要查
```

2. **全屏截图**（`Graphics.CopyFromScreen`）
```csharp
$g.CopyFromScreen($bounds.Location, [Point]::Empty, $bounds.Size)
```

3. **UIA 遍历**（验证元素存在和状态）

### 主题验证矩阵

每个窗口 × 每种主题都要验：

```
              Light              Dark
SettingsWindow   #FFF3F4F7    #FF15171C
AboutWindow      #FFF3F4F7    #FF15171C
AppUpdateWindow  #FFF3F4F7    #FF15171C
```

---

## 八、可复用的结论

1. **深色主题 = 全部重写控件模板**，没有捷径
2. **配色键用语义命名**（`Fg`/`Panel` 而非 `White`/`Gray`）
3. **次要信息收进菜单**，不占固定宽度
4. **所有 > 100ms 操作都要有转圈**
5. **遮罩只盖内容区**，不要动 `Window.Content` 和 `IsEnabled`
6. **自绘标题栏必加 `IsHitTestVisibleInChrome`**，且拖动要排除按钮
7. **错误框要能复制**
8. **状态用颜色+文字双编码**
9. **视觉验证读属性，不要只信截图**
