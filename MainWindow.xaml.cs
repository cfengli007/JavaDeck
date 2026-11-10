using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JavaDeck.Services;

namespace JavaDeck;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private JdkStore _store;
    private readonly LibericaClient _api = new();

    private List<InstalledJdk> _installed = new();
    private readonly Dictionary<int, string> _latestByFeature = new();
    private List<UpdateInfo> _pendingUpdates = new();
    private CancellationTokenSource? _cts;

    private InstallQueue _queue = null!;
    private UpdateChecker _updates = null!;
    private UpdateSettings _updateSettings = UpdateSettings.Load();
    private AppSettingsEx _appSettings = AppSettingsEx.Load();
    private TrayIcon? _tray;
    private bool _reallyExit;

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        _store = new JdkStore(_settings.RootDir);
        RootLabel.Text = _settings.RootDir;

        _queue = new InstallQueue(_store, _api);
        _queue.Jobs.CollectionChanged += (_, _) => Dispatcher.Invoke(UpdateQueueBars);
        _queue.Installed += jdk => Dispatcher.Invoke(() =>
        {
            _installed.RemoveAll(j => string.Equals(j.Path, jdk.Path, StringComparison.OrdinalIgnoreCase));
            // 队列可能已删掉同版本的旧构建，重新读一遍保持一致
            _installed = _store.LoadAll();
            if (_installed.All(x => x.Path != jdk.Path)) _installed.Add(jdk);
            _store.SaveAll(_installed);
            ReloadInstalled();
            SetStatus($"Java {jdk.FeatureVersion} {jdk.Version} 安装完成");
        });
        _queue.JobFinished += job => Dispatcher.Invoke(UpdateQueueBars);

        _updates = new UpdateChecker(_api, _store);
        QueueList.ItemsSource = _queue.Jobs;

        // 窗口句柄就绪后：托盘 + 圆角 + 深色框架
        SourceInitialized += (_, _) =>
        {
            try { WindowEffects.Apply(this); } catch { }
            try { ThemeManager.ApplyToWindow(this, ThemeManager.IsDarkEffective); } catch { }
            SetTray();
        };
        Loaded += (_, _) => _ = BootAsync();
        Closing += OnWindowClosing;
        Closed += (_, _) =>
        {
            try { _cts?.Cancel(); } catch { }
            try { _tray?.Dispose(); } catch { }
        };
    }

    // ==================== 标题栏 ====================
    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        // 点在按钮上时不拖动——否则 DragMove 会抢走鼠标捕获，按钮点不动
        if (IsInButton(e.OriginalSource as DependencyObject)) return;

        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        if (e.ButtonState != MouseButtonState.Pressed) return;

        // 最大化时拖拽应先还原再跟随，否则会抛异常或位置错乱
        if (WindowState == WindowState.Maximized)
        {
            var pos = e.GetPosition(this);
            var ratio = pos.X / Math.Max(1, ActualWidth);
            WindowState = WindowState.Normal;

            var workArea = SystemParameters.WorkArea;
            Left = Math.Max(workArea.Left, pos.X + Left - ActualWidth * ratio);
            Top = Math.Max(workArea.Top, 0);
        }

        try { DragMove(); } catch { }
    }

    /// <summary>判断某个视觉元素是否位于按钮内部（用于区分「拖窗口」和「点按钮」）。</summary>
    private static bool IsInButton(DependencyObject? source)
    {
        var cur = source;
        for (var i = 0; i < 12 && cur != null; i++)
        {
            if (cur is System.Windows.Controls.Primitives.ButtonBase) return true;
            cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
        }
        return false;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            return;
        }
        // WindowStyle=None + WindowChrome 时最大化会盖住任务栏，需按工作区手动设尺寸
        WindowState = WindowState.Maximized;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_reallyExit || _tray == null) return;
        e.Cancel = true;
        HideToTray();
    }

    // ==================== 托盘 ====================
    private void SetTray()
    {
        try
        {
            _tray = new TrayIcon { Tooltip = "JavaDeck" };
            var iconPath = Path.Combine(AppContext.BaseDirectory, "JavaDeck.ico");
            if (_tray.Create(this, iconPath))
            {
                _tray.DoubleClick += () => Dispatcher.Invoke(RestoreFromTray);
                _tray.RightClick += () => Dispatcher.Invoke(ShowTrayMenu);
            }
            else { _tray.Dispose(); _tray = null; }
        }
        catch { _tray = null; }
    }

    private void ShowTrayMenu()
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "打开 JavaDeck" };
        open.Click += (_, _) => RestoreFromTray();
        menu.Items.Add(open);

        var check = new MenuItem { Header = "检查更新" };
        check.Click += async (_, _) => await CheckUpdatesAsync();
        menu.Items.Add(check);

        menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => { _reallyExit = true; Close(); };
        menu.Items.Add(exit);

        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void HideToTray()
    {
        Hide();
        SetStatus("已最小化到托盘，双击图标可重新打开");
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    // ==================== 启动 ====================
    private async Task BootAsync()
    {
        // 主题已在 App.OnStartup 里应用（会尊重 --theme 命令行覆盖），这里不再重复，
        // 否则会把命令行指定的 dark 用配置文件里的 system 覆盖掉。
        try { NetworkSettings.Configure(_appSettings); } catch { }

        SetStatus("正在读取本地 JDK…");
        ShowBusy(true, "正在启动…");

        try
        {
            // 清理上次中断留下的 .staging-* 垃圾目录
            var cleaned = await Task.Run(() => _store.CleanStaging());
            if (cleaned > 0) SetStatus($"已清理 {cleaned} 个残留临时目录");

            await BootCoreAsync();
        }
        finally { ShowBusy(false, null); }
    }

    private async Task BootCoreAsync()
    {
        var localList = await Task.Run(() => _store.LoadAll());
        if (localList.Count == 0)
        {
            var discovered = await Task.Run(() => JdkScanner.Scan(Array.Empty<string>()));
            if (discovered.Count > 0)
            {
                localList = discovered.Select(h => new InstalledJdk
                {
                    Id = "auto-" + h.Feature + "-" + Path.GetFileName(h.Path.TrimEnd('\\')),
                    FeatureVersion = h.Feature,
                    Version = h.Version,
                    Path = h.Path,
                    InstalledAt = DateTime.Now,
                }).ToList();
                _store.SaveAll(localList);
            }
        }
        _installed = localList;
        ReloadInstalled();

        SyncUpdateUi();
        if (_updateSettings.Enabled)
        {
            _updates.Start(
                TimeSpan.FromHours(Math.Max(1, _updateSettings.IntervalHours)),
                infos => Dispatcher.Invoke(() => ApplyUpdateResult(infos)),
                _ => { });
        }
        else SetStatus("就绪");

        UpdateEnvSummary();
    }

    /// <summary>
    /// 刷新版本信息。按官方 API 文档：
    ///   • 不拉取全量列表，也不使用缓存
    ///   • 对每个已装 JDK 查一次 latestInFeatureVersion（约 640 字节）
    ///   • 仅在「添加 JDK」时才拉全量列表
    /// </summary>
    private async Task RefreshUpdateInfoAsync(bool silentIfNone = true)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var infos = await Task.Run(() => _updates.CheckAsync(cts.Token), cts.Token);
            ApplyUpdateResult(infos);
        }
        catch (OperationCanceledException) { if (!silentIfNone) SetStatus("检查超时"); }
        catch (Exception ex) { if (!silentIfNone) SetStatus("检查失败：" + ex.Message); }
    }

    private void UpdateEnvSummary()
    {
        var home = EnvManager.GetJdkHome(out var isSystem);
        if (string.IsNullOrEmpty(home))
        {
            EnvSummary.Text = "JAVA_HOME 未设置";
            return;
        }
        var name = Path.GetFileName(home.TrimEnd('\\'));
        EnvSummary.Text = $"JAVA_HOME → {name}" + (isSystem ? "（系统级）" : "");
    }

    // ==================== 列表 ====================
    private void ReloadInstalled()
    {
        JdkList.Children.Clear();

        foreach (var jdk in _installed.OrderByDescending(j => j.FeatureVersion))
            JdkList.Children.Add(BuildRow(jdk));

        foreach (var fv in new[] { 25, 21, 17, 11, 8 })
        {
            if (_installed.Any(j => j.FeatureVersion == fv)) continue;
            JdkList.Children.Add(BuildPlaceholder(fv));
        }

        if (_installed.Count == 0)
        {
            JdkList.Children.Add(new TextBlock
            {
                Text = "点底部「导入已有」接管本机已装的 JDK，或「添加 JDK」从官方源安装",
                Foreground = (Brush)FindResource("FgDim"),
                FontSize = 12,
                Margin = new Thickness(8, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        UpdateEnvSummary();
    }

    private Border BuildPlaceholder(int feature)
    {
        var card = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(14, 10, 14, 10),
            Opacity = 0.6,
        };
        var g = new Grid();
        g.Children.Add(new TextBlock
        {
            Text = "Java " + feature,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("FgDim"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var btn = new Button
        {
            Content = "安装",
            Style = (Style)FindResource("PrimaryButton"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(16, 4, 16, 4),
        };
        var captured = feature;
        btn.Click += (_, _) => _ = QuickInstallAsync(captured);
        g.Children.Add(btn);
        card.Child = g;
        return card;
    }

    private Border BuildRow(InstalledJdk jdk)
    {
        var card = new Border
        {
            Background = (Brush)FindResource("Card"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 9, 14, 9),
            Margin = new Thickness(0, 0, 0, 8),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFE)),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 2, 8, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        badge.Child = new TextBlock
        {
            Text = "Java " + jdk.FeatureVersion,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x4F, 0xB0)),
        };
        Grid.SetColumn(badge, 0);
        grid.Children.Add(badge);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 0) };

        // 版本号 + 变体徽标
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = jdk.Version,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var (varBg, varFg, varText) = jdk.Variant switch
        {
            "full" => (Color.FromRgb(0xF3, 0xE6, 0xFB), Color.FromRgb(0x6B, 0x2E, 0x9B), "Full · 含 JavaFX"),
            "lite" => (Color.FromRgb(0xE8, 0xF5, 0xEC), Color.FromRgb(0x1E, 0x7A, 0x46), "Lite · 精简"),
            "standard" => (Color.FromRgb(0xE8, 0xF0, 0xFE), Color.FromRgb(0x1F, 0x4F, 0xB0), "标准"),
            _ => (Color.FromRgb(0xF0, 0xF1, 0xF4), Color.FromRgb(0x6B, 0x72, 0x82), "变体未知"),
        };
        var varBadge = new Border
        {
            Background = new SolidColorBrush(varBg),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        varBadge.Child = new TextBlock
        {
            Text = varText,
            FontSize = 10,
            Foreground = new SolidColorBrush(varFg),
        };
        titleRow.Children.Add(varBadge);

        info.Children.Add(titleRow);
        info.Children.Add(new TextBlock
        {
            Text = jdk.Path,
            FontSize = 11,
            Foreground = (Brush)FindResource("FgDim"),
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        if (IsJdkActive(jdk))
        {
            var tag = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x3A, 0x2C)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(9, 3, 9, 3),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            tag.Child = new TextBlock { Text = "JAVA_HOME", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x6F, 0xD8, 0xA0)) };
            actions.Children.Add(tag);
        }
        else
        {
            var use = new Button { Content = "设为 JAVA_HOME", Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(0, 0, 6, 0) };
            use.Click += (_, _) => UseJdk(jdk);
            actions.Children.Add(use);
        }

        // 标记需要更新的版本
        var upd = FindUpdateFor(jdk);
        if (upd != null)
        {
            var badge2 = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x4A, 0x35, 0x18)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"可更新到 {upd.LatestVersion}",
            };
            badge2.Child = new TextBlock
            {
                Text = "可更新 → " + upd.LatestVersion.Split('+')[0],
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D)),
            };
            actions.Children.Add(badge2);

            var btnUpd = new Button
            {
                Content = "更新",
                Style = (Style)FindResource("PrimaryButton"),
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(12, 4, 12, 4),
            };
            btnUpd.Click += (_, _) => UpdateSingle(jdk, upd);
            actions.Children.Add(btnUpd);
        }

        var menu = new Button { Content = "⋯", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(10, 0, 10, 2), FontSize = 15 };
        menu.Click += (_, _) => ShowRowMenu(menu, jdk);
        actions.Children.Add(menu);

        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);
        card.Child = grid;
        return card;
    }

    /// <summary>找出这个 JDK 对应的可用更新（同大版本、更新的构建）。</summary>
    private UpdateInfo? FindUpdateFor(InstalledJdk jdk) =>
        _pendingUpdates.FirstOrDefault(u =>
            u.Feature == jdk.FeatureVersion &&
            LibericaClient.BuildKey(u.LatestVersion) > LibericaClient.BuildKey(jdk.Version));

    private void UpdateSingle(InstalledJdk jdk, UpdateInfo upd)
    {
        if (upd.Release == null) { SetStatus("没有可用的更新包"); return; }
        _queue.Enqueue(new[] { upd.Release }, JobKind.Update, _ => jdk);
        QueuePanel.Visibility = Visibility.Visible;
        UpdateQueueBars();
        SetStatus($"已加入队列：Java {jdk.FeatureVersion} 更新到 {upd.LatestVersion}");
    }

    private bool IsJdkActive(InstalledJdk jdk)
    {
        var home = EnvManager.GetJdkHome(out _);
        if (string.IsNullOrEmpty(home)) return false;
        if (string.Equals(home, jdk.Path, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            if (!Directory.Exists(home)) return false;
            var di = new DirectoryInfo(home);
            if (!di.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            var real = Junction.ResolveTarget(home);
            return string.Equals(real.TrimEnd('\\'), jdk.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private void ShowRowMenu(Button anchor, InstalledJdk jdk)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };

        var use = new MenuItem { Header = "设为 JAVA_HOME" };
        use.Click += (_, _) => UseJdk(jdk);
        menu.Items.Add(use);

        var open = new MenuItem { Header = "在资源管理器中打开" };
        open.Click += (_, _) => OpenPath(jdk.Path);
        menu.Items.Add(open);

        var copy = new MenuItem { Header = "复制路径" };
        copy.Click += (_, _) => { try { Clipboard.SetText(jdk.Path); SetStatus("路径已复制"); } catch { } };
        menu.Items.Add(copy);

        var promote = new MenuItem { Header = "在 PATH 中上移" };
        promote.Click += (_, _) => MoveInPath(jdk, -1);
        menu.Items.Add(promote);

        var demote = new MenuItem { Header = "在 PATH 中下移" };
        demote.Click += (_, _) => MoveInPath(jdk, +1);
        menu.Items.Add(demote);

        menu.Items.Add(new Separator());

        var del = new MenuItem { Header = "卸载并删除文件…" };
        del.Click += (_, _) => Uninstall(jdk);
        menu.Items.Add(del);

        menu.IsOpen = true;
    }

    private void UseJdk(InstalledJdk jdk)
    {
        using var busy = new BusyOverlay(ContentHost, $"正在切换到 Java {jdk.FeatureVersion}…");
        try
        {
            EnvManager.SwitchJavaHome(jdk.Path);
            ReloadInstalled();
            SetStatus($"JAVA_HOME 已切换到 Java {jdk.FeatureVersion}（{jdk.Version}），该版本已从 PATH 移除");
        }
        catch (Exception ex)
        {
            MsgWindow.Show(this, "提示", "切换失败：" + ex.Message, false);
        }
    }

    private void MoveInPath(InstalledJdk jdk, int delta)
    {
        try
        {
            var bin = Path.Combine(jdk.Path, "bin");
            var raw = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
            var parts = raw.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

            var idx = parts.FindIndex(p => string.Equals(p.TrimEnd('\\'), bin.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            if (idx < 0) { SetStatus("该 JDK 不在用户 PATH 中"); return; }

            var newIdx = Math.Clamp(idx + delta, 0, parts.Count - 1);
            if (newIdx == idx) return;

            var item = parts[idx];
            parts.RemoveAt(idx);
            parts.Insert(newIdx, item);

            Environment.SetEnvironmentVariable("PATH", string.Join(";", parts), EnvironmentVariableTarget.User);
            EnvManager.Broadcast();
            SetStatus($"已移动 Java {jdk.FeatureVersion} 到第 {newIdx + 1} 位");
        }
        catch (Exception ex) { SetStatus("失败：" + ex.Message); }
    }

    private void Uninstall(InstalledJdk jdk)
    {
        var active = IsJdkActive(jdk);
        var msg = $"确定卸载 Java {jdk.FeatureVersion}（{jdk.Version}）吗？\n\n{jdk.Path}\n\n目录将被永久删除。";
        if (active) msg += "\n\n注意：这是当前 JAVA_HOME，卸载后 JAVA_HOME 会被清空。";

        if (!MsgWindow.Confirm(this, "确认卸载", msg)) return;

        _queue.EnqueueUninstall(jdk);
        QueuePanel.Visibility = Visibility.Visible;
        UpdateQueueBars();
        SetStatus($"已加入卸载队列：Java {jdk.FeatureVersion}");
    }

    // ==================== 队列 ====================
    private void UpdateQueueBars()
    {
        var active = _queue.Jobs.Count(j => !j.IsFinished);
        var total = _queue.Jobs.Count;

        if (total == 0)
        {
            QueuePanel.Visibility = Visibility.Collapsed;
            QueueBadge.Visibility = Visibility.Collapsed;
            return;
        }

        QueuePanel.Visibility = Visibility.Visible;
        QueueBadge.Visibility = Visibility.Visible;

        var running = _queue.Jobs.Count(j => j.State is JobState.Downloading or JobState.Extracting);
        var done = _queue.Jobs.Count(j => j.State == JobState.Done);
        var failed = _queue.Jobs.Count(j => j.State == JobState.Failed);

        QueueBadge.Content = $"队列 {done}/{total}";
        QueueTitle.Text = $"安装队列 · 进行中 {running} · 完成 {done}" + (failed > 0 ? $" · 失败 {failed}" : "");
    }

    private void OnToggleQueue(object sender, RoutedEventArgs e)
    {
        QueuePanel.Visibility = QueuePanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnClearFinished(object sender, RoutedEventArgs e)
    {
        _queue.ClearFinished();
        UpdateQueueBars();
    }

    private void OnCancelJob(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: InstallJob job }) _queue.Cancel(job);
    }

    // ==================== 更新 ====================
    private void ApplyUpdateResult(List<UpdateInfo> infos)
    {
        _pendingUpdates = infos;
        _latestByFeature.Clear();
        foreach (var i in infos) _latestByFeature[i.Feature] = i.LatestVersion;

        _updateSettings.LastCheck = DateTime.Now;
        _updateSettings.Save();

        ReloadInstalled();
        SyncUpdateUi();

        // 只有真的有更新才显示
        if (infos.Count == 0)
        {
            UpdateBadge.Visibility = Visibility.Collapsed;
            MenuUpdateAll.Visibility = Visibility.Collapsed;
            SetStatus("已是最新版本");
        }
        else
        {
            UpdateBadge.Content = $"{infos.Count} 个可更新";
            UpdateBadge.Visibility = Visibility.Visible;
            MenuUpdateAll.Header = $"全部更新（{infos.Count}）";
            MenuUpdateAll.Visibility = Visibility.Visible;
            SetStatus($"发现 {infos.Count} 个 JDK 有更新");
        }
    }

    private void SyncUpdateUi()
    {
        MenuAutoUpdate.IsChecked = _updateSettings.Enabled;
        // 检查频率始终可选——即使自动检查关着，用户也可能先设定频率再开启
        MenuInterval.IsEnabled = true;

        foreach (var (item, hours) in new (MenuItem, int)[]
        {
            (MenuInterval6, 6), (MenuInterval12, 12), (MenuInterval24, 24),
            (MenuInterval72, 72), (MenuInterval168, 168),
        })
            item.IsChecked = _updateSettings.IntervalHours == hours;

        LastCheckText.Text = _updateSettings.LastCheck.HasValue
            ? "上次检查 " + _updateSettings.LastCheck.Value.ToString("MM-dd HH:mm")
            : "尚未检查";
    }

    private void RestartUpdateTimer()
    {
        _updates.Stop();
        if (!_updateSettings.Enabled) return;
        _updates.Start(TimeSpan.FromHours(Math.Max(1, _updateSettings.IntervalHours)),
            infos => Dispatcher.Invoke(() => ApplyUpdateResult(infos)), _ => { });
    }

    private void OnToggleAutoUpdate(object sender, RoutedEventArgs e)
    {
        _updateSettings.Enabled = MenuAutoUpdate.IsChecked;
        if (!_updateSettings.Save())
            SetStatus("设置无法保存：" + (UpdateSettings.SaveError ?? "权限不足"));
        RestartUpdateTimer();
        SetStatus(_updateSettings.Enabled
            ? $"自动检查已开启（每 {FormatInterval(_updateSettings.IntervalHours)}）"
            : "自动检查已关闭");
    }

    private void OnPickInterval(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !int.TryParse(tag, out var hours)) return;

        _updateSettings.IntervalHours = hours;
        if (!_updateSettings.Save())
            SetStatus("设置无法保存：" + (UpdateSettings.SaveError ?? "权限不足"));

        SyncUpdateUi();
        RestartUpdateTimer();
        SetStatus($"自动检查频率已设为每 {FormatInterval(hours)}");
    }

    private static string FormatInterval(int hours) => hours switch
    {
        < 24 => hours + " 小时",
        _ => (hours / 24) + " 天",
    };

    private async Task CheckUpdatesAsync()
    {
        SetStatus("正在检查更新…");
        ShowBusy(true, "正在检查更新…");
        try { await RefreshUpdateInfoAsync(silentIfNone: false); }
        finally { ShowBusy(false, null); }
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e) => await CheckUpdatesAsync();

    private void OnShowUpdates(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdates.Count == 0) { SetStatus("没有可更新的 JDK"); return; }
        var lines = _pendingUpdates.Select(u => $"Java {u.Feature}: {u.InstalledVersion} → {u.LatestVersion}");
        MsgWindow.Show(this, "可用更新", string.Join("\n", lines), false);
    }

    private void OnUpdateAll(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdates.Count == 0) { SetStatus("没有可更新的 JDK"); return; }
        var releases = _pendingUpdates.Where(u => u.Release != null).Select(u => u.Release!).ToList();
        _queue.Enqueue(releases, JobKind.Update, rel =>
            _installed.FirstOrDefault(j => j.FeatureVersion == rel.FeatureVersion));
        QueuePanel.Visibility = Visibility.Visible;
        SetStatus($"已把 {releases.Count} 个更新加入队列（安装完成后自动删除旧版本）");
        UpdateQueueBars();
    }

    // ==================== 菜单动作 ====================
    /// <summary>
    /// 在按钮正下方打开菜单。
    /// 直接设 IsOpen=true 而不指定 PlacementTarget，WPF 会把菜单位置算成屏幕左上角。
    /// </summary>
    private void OpenMenu(Button button)
    {
        var menu = button.ContextMenu;
        if (menu == null) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.HorizontalOffset = 0;
        menu.VerticalOffset = 0;
        menu.IsOpen = true;
    }

    private void OnMenuFile(object sender, RoutedEventArgs e) => OpenMenu((Button)sender);
    private void OnMenuJdk(object sender, RoutedEventArgs e) => OpenMenu((Button)sender);
    private void OnMenuEnv(object sender, RoutedEventArgs e) => OpenMenu((Button)sender);
    private void OnMenuUpdate(object sender, RoutedEventArgs e) => OpenMenu((Button)sender);
    private void OnMenuTools(object sender, RoutedEventArgs e) => OpenMenu((Button)sender);

    private void OnCheckAppUpdate(object sender, RoutedEventArgs e)
    {
        var w = new AppUpdateWindow { Owner = this };
        w.ShowDialog();
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        var body =
            "JavaDeck  " + SelfUpdater.CurrentVersion + "\n" +
            "JDK 版本管理器 · Windows 原生\n\n" +
            "技术栈：WPF / .NET 8\n" +
            "JDK 来源：BellSoft Liberica（LTS 8/11/17/21/25）\n\n" +
            "许可证：GPL-3.0\n" +
            "Copyright (C) 2026 JavaDeck Contributors\n\n" +
            "项目地址：\n" + SelfUpdater.RepoUrl + "\n\n" +
            "本程序是自由软件，依据 GNU 通用公共许可证第 3 版发布；\n" +
            "你可以自由复制、修改和再分发，但不提供任何担保。";
        var w = new MsgWindow("关于 JavaDeck", body, false) { Owner = this };
        w.ShowDialog();
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_appSettings) { Owner = this };
        dlg.ShowDialog();
        if (dlg.Changed)
        {
            TryFindResource("Bg");
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("Bg");
            ThemeManager.ApplyToWindow(this, ThemeManager.IsDarkEffective);
            SetStatus("设置已保存");
        }
    }

    private void OnExit(object sender, RoutedEventArgs e) { _reallyExit = true; Close(); }

    private void OnManageJdk(object sender, RoutedEventArgs e)
    {
        var dlg = new JdkManageWindow(_store, _installed, this) { Owner = this };
        dlg.ShowDialog();
        if (dlg.Changed) { _installed = _store.LoadAll(); ReloadInstalled(); }
    }

    private void OnJavaHomeSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new JavaHomeWindow(_installed) { Owner = this };
        dlg.ShowDialog();
        if (dlg.Changed) ReloadInstalled();
    }

    private void OnManagePath(object sender, RoutedEventArgs e)
    {
        var dlg = new PathWindow(_installed) { Owner = this };
        dlg.ShowDialog();
        ReloadInstalled();
    }

    private void OnShowEnv(object sender, RoutedEventArgs e)
    {
        var home = EnvManager.GetJdkHome(out var isSystem);
        var ver = string.IsNullOrEmpty(home) ? "(未设置)" : (EnvManager.ProbeJavaVersion(home) ?? "(无法执行)");
        MsgWindow.Show(this, "当前环境", $"JAVA_HOME = {home ?? "(未设置)"}{(isSystem ? "  [系统级]" : "  [用户级]")}\n\njava -version:\n{ver}", false);
    }

    private void OnShowMc(object sender, RoutedEventArgs e)
    {
        var w = new McWindow(_installed) { Owner = this };
        w.ShowDialog();
    }

    private void OnAddJdk(object sender, RoutedEventArgs e)
    {
        var dlg = new AddJdkWindow(_api, _installed, _queue, _settings.RootDir) { Owner = this };
        dlg.ShowDialog();
        UpdateQueueBars();
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        var dlg = new ImportWindow(_installed, _store) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Imported.Count == 0) return;
        foreach (var j in dlg.Imported) _installed.Add(j);
        _store.SaveAll(_installed);
        ReloadInstalled();
        SetStatus($"已导入 {dlg.Imported.Count} 个 JDK");
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        _installed = await Task.Run(() => _store.LoadAll());
        ReloadInstalled();
        SetStatus("已刷新");
    }

    private void OnChooseRoot(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择 JDK 安装目录", InitialDirectory = _settings.RootDir };
        if (dlg.ShowDialog() != true) return;

        _settings.RootDir = dlg.FolderName;
        _settings.Save();
        _store = new JdkStore(_settings.RootDir);
        RootLabel.Text = _settings.RootDir;
        _installed = _store.LoadAll();
        ReloadInstalled();
        SetStatus("安装目录已设为 " + _settings.RootDir);
    }

    private void OnOpenRoot(object sender, RoutedEventArgs e) => OpenPath(_settings.RootDir);

    private static void OpenPath(string path)
    {
        try
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch { }
    }

    // ==================== 快速安装 ====================
    private async Task QuickInstallAsync(int feature)
    {
        try
        {
            SetStatus($"正在查询 Java {feature}…");
            var all = await Task.Run(() => _api.GetAllAsync(feature));
            var latest = _api.PickLatest(all, "standard");
            if (latest == null) { SetStatus("没有找到可用版本"); return; }
            _queue.Enqueue(new[] { latest });
            QueuePanel.Visibility = Visibility.Visible;
            UpdateQueueBars();
            SetStatus($"已加入队列：Java {feature} {latest.Version}");
        }
        catch (Exception ex) { SetStatus("失败：" + ex.Message); }
    }

    private System.Windows.Threading.DispatcherTimer? _busyTimer;

    private void ShowBusy(bool show, string? message)
    {
        if (show)
        {
            BusyText.Text = message ?? "加载中…";
            BusyLayer.Visibility = Visibility.Visible;

            _busyTimer ??= new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(40) };
            if (!_busyTimer.IsEnabled)
            {
                _busyTimer.Tick += (_, _) =>
                {
                    BusyRotate.Angle = (BusyRotate.Angle + 16) % 360;
                };
                _busyTimer.Start();
            }
        }
        else
        {
            BusyLayer.Visibility = Visibility.Collapsed;
            _busyTimer?.Stop();
        }
    }

    private void SetStatus(string text) => StatusText.Text = text;
}