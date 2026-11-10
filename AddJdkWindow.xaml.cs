using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using JavaDeck.Services;

namespace JavaDeck;

public partial class AddJdkWindow : Window
{
    private readonly LibericaClient _api;
    private readonly List<InstalledJdk> _installed;
    private readonly InstallQueue _queue;
    private readonly string _root;
    private List<LibericaRelease> _cache = new();
    private bool _loading;
    private static readonly int[] Features = { 0, 8, 11, 17, 21, 25 };   // 0 = ALL

    public List<LibericaRelease> SelectedReleases { get; } = new();

    public AddJdkWindow(LibericaClient api, List<InstalledJdk> installed, InstallQueue queue, string root)
    {
        InitializeComponent();
        _api = api;
        _installed = installed;
        _queue = queue;
        _root = root;

        RootPathText.Text = "安装目录：" + root;
        QueueList.ItemsSource = queue.Jobs;

        queue.Jobs.CollectionChanged += (_, _) =>
            QueuePanel.Visibility = queue.Jobs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var f in Features) FeatureCombo.Items.Add(f == 0 ? "ALL" : "Java " + f);
        FeatureCombo.SelectedIndex = 0;   // 默认 ALL
        Loaded += async (_, _) => await LoadAsync();
    }

    private int CurrentFeature => Features[Math.Max(0, FeatureCombo.SelectedIndex)];
    private bool IsAll => CurrentFeature == 0;

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;

        try
        {
            // 每次打开都拉取最新列表，不缓存
            HintText.Text = IsAll ? "正在获取全部 LTS 版本…" : "正在获取版本列表…";
            ShowLoading(true);
            VersionList.Items.Clear();
            _cache.Clear();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            if (IsAll)
            {
                var features = Features.Where(f => f > 0).ToList();
                var results = new List<LibericaRelease>();
                var done = 0;

                // 并发拉取，每完成一个更新提示
                var tasks = features.Select(async f =>
                {
                    try
                    {
                        var list = await _api.GetAllAsync(f, cts.Token);
                        lock (results) { results.AddRange(list); }
                        var n = System.Threading.Interlocked.Increment(ref done);
                        Dispatcher.Invoke(() => HintText.Text = $"已获取 {n}/{features.Count} 个 LTS 版本…");
                    }
                    catch { }
                });
                await Task.WhenAll(tasks);
                _cache = results;
            }
            else
            {
                _cache = await _api.GetAllAsync(CurrentFeature, cts.Token);
            }

            ApplyFilter();
            HintText.Text = _cache.Count == 0
                ? "获取失败：网络不可达"
                : $"{_cache.Count} 个版本可选（可多选，直接点击即可）";
        }
        catch (OperationCanceledException) { HintText.Text = "获取超时，请检查网络"; }
        catch (Exception ex) { HintText.Text = "获取失败：" + ex.Message; }
        finally
        {
            ShowLoading(false);
            _loading = false;
        }
    }

    private System.Windows.Threading.DispatcherTimer? _spinTimer;

    private void ShowLoading(bool show)
    {
        LoadingLayer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Cursor = show ? System.Windows.Input.Cursors.Wait : null;

        if (show)
        {
            _spinTimer ??= new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(40) };
            if (!_spinTimer.IsEnabled)
            {
                _spinTimer.Tick += (_, _) => LoadingRotate.Angle = (LoadingRotate.Angle + 16) % 360;
                _spinTimer.Start();
            }
        }
        else _spinTimer?.Stop();
    }

    private List<string> ActiveVariants()
    {
        var v = new List<string>();
        if (VarStandard.IsChecked == true) v.Add("standard");
        if (VarFull.IsChecked == true) v.Add("full");
        if (VarLite.IsChecked == true) v.Add("lite");
        return v;
    }

    private void ApplyFilter()
    {
        VersionList.Items.Clear();
        var variants = ActiveVariants();
        if (variants.Count == 0) { HintText.Text = "至少勾选一种变体"; InstallButton.IsEnabled = false; return; }

        // 关键：必须同时匹配「大版本 + 版本号 + 变体」，否则同版本的标准/Full/Lite 全被标成已安装
        var installedKeys = _installed
            .Where(i => !string.IsNullOrEmpty(i.Variant))
            .Select(i => i.FeatureVersion + "|" + i.Version + "|" + i.Variant)
            .ToHashSet();
        var onlyLatest = OnlyLatest.IsChecked == true;

        var rows = _cache
            .Where(r => variants.Contains(r.Variant))
            .Where(r => !onlyLatest || r.LatestInFeatureVersion)
            .OrderByDescending(r => LibericaClient.BuildKey(r.Version))
            .ThenBy(r => r.Variant)
            .ToList();

        foreach (var r in rows)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

            var left = new StackPanel();
            left.Children.Add(new TextBlock { Text = r.Version + "  ·  " + r.VariantLabel, FontSize = 12 });
            var tags = new List<string>();
            if (r.Lts) tags.Add("LTS");
            if (r.LatestInFeatureVersion) tags.Add("最新");
            tags.Add(r.FileName);
            left.Children.Add(new TextBlock
            {
                Text = string.Join("  ·  ", tags),
                FontSize = 10,
                Foreground = (System.Windows.Media.Brush)FindResource("FgDim"),
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(left, 0);
            g.Children.Add(left);

            var size = new TextBlock
            {
                Text = $"{r.Size / 1048576.0:F0} MB",
                FontSize = 11,
                Foreground = (System.Windows.Media.Brush)FindResource("FgDim"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(size, 1);
            g.Children.Add(size);

            var already = installedKeys.Contains(r.FeatureVersion + "|" + r.Version + "|" + r.Variant);
            var status = new TextBlock
            {
                Text = already ? "已安装" : "",
                FontSize = 11,
                Foreground = (System.Windows.Media.Brush)FindResource(already ? "Ok" : "FgFaint"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(status, 2);
            g.Children.Add(status);

            var item = new ListBoxItem { Content = g, Tag = r };
            VersionList.Items.Add(item);
        }

        HintText.Text = rows.Count == 0
            ? "没有匹配的版本（试试勾选其它变体或取消「只看最新」）"
            : $"{rows.Count} 个可选 · 可多选后加入队列";
        UpdateInstallButton();
    }

    private void UpdateInstallButton()
    {
        var count = VersionList.SelectedItems.Count;
        InstallButton.IsEnabled = count > 0;
        InstallButton.Content = count > 1 ? $"加入队列（{count}）" : "加入队列";

        if (SelectAllBtn != null)
            SelectAllBtn.Content = (VersionList.Items.Count > 0 && count == VersionList.Items.Count)
                ? "取消全选" : "全选";
    }

    private void OnFeatureChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _ = LoadAsync();
    }

    private void OnVariantChanged(object sender, RoutedEventArgs e)
    {
        if (_cache.Count > 0) ApplyFilter();
    }

    /// <summary>一键全选当前筛选出的全部版本（跨所有已勾选的变体）。</summary>
    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        if (VersionList.Items.Count == 0) return;

        if (VersionList.SelectedItems.Count == VersionList.Items.Count)
        {
            VersionList.UnselectAll();
            SelectAllBtn.Content = "全选";
        }
        else
        {
            VersionList.SelectAll();
            SelectAllBtn.Content = "取消全选";
        }
        UpdateInstallButton();
    }

    private void OnInstall(object sender, RoutedEventArgs e)
    {
        SelectedReleases.Clear();
        foreach (var item in VersionList.SelectedItems)
            if (item is ListBoxItem { Tag: LibericaRelease r })
                SelectedReleases.Add(r);

        if (SelectedReleases.Count == 0) return;

        _queue.Enqueue(SelectedReleases);
        QueuePanel.Visibility = Visibility.Visible;
        HintText.Text = $"已加入 {SelectedReleases.Count} 个任务，可在队列中查看进度";
        VersionList.UnselectAll();
        UpdateInstallButton();
    }

    private void OnCancelJob(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: InstallJob job })
            _queue.Cancel(job);
    }

    private void OnClearFinished(object sender, RoutedEventArgs e) => _queue.ClearFinished();

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        // 有关键任务在跑时提示
        var running = _queue.Jobs.Count(j => !j.IsFinished);
        if (running > 0)
        {
            var ok = MsgWindow.Confirm(this, "确认", $"还有 {running} 个任务在队列中。\n关闭窗口不会中断它们，会在后台继续下载安装。\n\n确定关闭？");
        if (!ok) return;
        }
        DialogResult = false;
    }

    // 让 ListBox 选中变化时更新按钮
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        VersionList.SelectionChanged += (_, _) => UpdateInstallButton();
    }
}