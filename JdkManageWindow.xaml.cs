using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JavaDeck.Services;

namespace JavaDeck;

public partial class JdkManageWindow : Window
{
    private readonly JdkStore _store;
    private readonly List<InstalledJdk> _all;
    private List<InstalledJdk> _order;
    private InstalledJdk? _home;
    public bool Changed { get; private set; }

    public JdkManageWindow(JdkStore store, List<InstalledJdk> installed, Window owner)
    {
        InitializeComponent();
        _store = store;
        _all = installed.OrderByDescending(j => j.FeatureVersion).ToList();
        _home = _all.FirstOrDefault(IsCurrentHome);
        _order = BuildPathOrder(_all);
        RefreshHomeText();
        RefreshList();

        // 提示同大版本重复
        var dupes = _all.GroupBy(j => j.FeatureVersion).Where(g => g.Count() > 1).ToList();
        if (dupes.Count > 0)
        {
            var msg = string.Join("\n", dupes.Select(g =>
                $"Java {g.Key}: " + string.Join(", ", g.Select(x => x.Version))));
            Dispatcher.BeginInvoke(new Action(() =>
                MsgWindow.Show(this, "信息", "检测到同一大版本装了多个构建，会重复占用磁盘：\n\n" + msg +
                    "\n\n可以在主界面右键旧版本 →「卸载并删除文件」清理。", false)));
        }
    }

    private bool IsCurrentHome(InstalledJdk jdk)
    {
        var home = EnvManager.GetJdkHome(out _);
        if (string.IsNullOrEmpty(home)) return false;
        try
        {
            var real = Junction.ResolveTarget(home);
            return string.Equals(real.TrimEnd('\\'), jdk.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                || string.Equals(home.TrimEnd('\\'), jdk.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static List<InstalledJdk> BuildPathOrder(List<InstalledJdk> all)
    {
        var raw = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        var bins = raw.Split(';', StringSplitOptions.RemoveEmptyEntries)
                      .Select(p => p.Trim())
                      .ToList();

        var ordered = new List<InstalledJdk>();
        foreach (var bin in bins)
        {
            var normalized = bin.Replace("%JAVA_HOME%", "").TrimEnd('\\');
            foreach (var j in all)
            {
                if (ordered.Contains(j)) continue;
                var jbin = Path.Combine(j.Path, "bin");
                if (string.Equals(jbin.TrimEnd('\\'), normalized, StringComparison.OrdinalIgnoreCase))
                    ordered.Add(j);
            }
        }
        foreach (var j in all) if (!ordered.Contains(j)) ordered.Add(j);
        return ordered;
    }

    private void RefreshHomeText()
    {
        HomeText.Text = _home == null
            ? "未设置"
            : $"Java {_home.FeatureVersion}  ·  {_home.Version}\n{_home.Path}";
    }

    private void RefreshList()
    {
        JdkList.Items.Clear();
        var idx = 1;
        foreach (var jdk in _order)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var num = new TextBlock
            {
                Text = "#" + idx,
                FontSize = 11,
                Foreground = (Brush)FindResource("FgFaint"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(num, 0);
            g.Children.Add(num);

            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFE)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            badge.Child = new TextBlock
            {
                Text = "Java " + jdk.FeatureVersion,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x4F, 0xB0)),
            };
            Grid.SetColumn(badge, 1);
            g.Children.Add(badge);

            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
            sp.Children.Add(new TextBlock
            {
                Text = jdk.Version + "   ·   " + jdk.VariantLabel,
                FontSize = 12,
            });
            sp.Children.Add(new TextBlock
            {
                Text = jdk.Path,
                FontSize = 10,
                Foreground = (Brush)FindResource("FgDim"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(sp, 2);
            g.Children.Add(sp);

            var isHome = _home != null && _home.Path == jdk.Path;
            var rb = new RadioButton
            {
                Content = "设为 JAVA_HOME",
                IsChecked = isHome,
                Tag = jdk,
                GroupName = "JavaHomeChoice",
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
            };
            rb.Checked += (s, e) =>
            {
                if (s is RadioButton { Tag: InstalledJdk picked })
                {
                    _home = picked;
                    RefreshHomeText();
                }
            };
            Grid.SetColumn(rb, 3);
            g.Children.Add(rb);

            JdkList.Items.Add(new ListBoxItem { Content = g, Tag = jdk });
            idx++;
        }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
    }

    private InstalledJdk? Selected => JdkList.SelectedItem is ListBoxItem { Tag: InstalledJdk j } ? j : null;

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);
    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(+1);

    private void Move(int delta)
    {
        var sel = Selected;
        if (sel == null) return;
        var idx = _order.IndexOf(sel);
        var nidx = Math.Clamp(idx + delta, 0, _order.Count - 1);
        if (nidx == idx) return;
        _order.RemoveAt(idx);
        _order.Insert(nidx, sel);
        RefreshList();
        JdkList.SelectedIndex = nidx;
    }

    private void OnRemoveFromPath(object sender, RoutedEventArgs e)
    {
        var sel = Selected;
        if (sel == null) return;
        if (!MsgWindow.Confirm(this, "确认", $"从 PATH 中移除 Java {sel.FeatureVersion}？\n\n不会删除文件。")) return;
        _order.Remove(sel);
        RefreshList();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        using var busy = new BusyOverlay((Panel)Content, "正在应用环境变量…");
        try
        {
            // 1) JAVA_HOME
            if (_home != null)
                Environment.SetEnvironmentVariable("JAVA_HOME", _home.Path, EnvironmentVariableTarget.User);

            // 2) PATH：保留非 JAVA_HOME 的既有项，把 JDK bin 按顺序重排到最前
            var raw = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
            var existing = raw.Split(';', StringSplitOptions.RemoveEmptyEntries)
                              .Select(p => p.Trim())
                              .Where(p => p.Length > 0)
                              .Where(p => !EnvManager.LooksLikeJdkBin(p)
                                       && !string.Equals(p.TrimEnd('\\'), "%JAVA_HOME%\\bin", StringComparison.OrdinalIgnoreCase))
                              .ToList();

            var jdkBins = _order.Select(j => Path.Combine(j.Path, "bin")).ToList();
            var prefix = _home != null ? new List<string> { "%JAVA_HOME%\\bin" } : new List<string>();

            var final = prefix.Concat(jdkBins).Concat(existing)
                              .Distinct(StringComparer.OrdinalIgnoreCase)
                              .ToList();

            Environment.SetEnvironmentVariable("PATH", string.Join(";", final), EnvironmentVariableTarget.User);
            EnvManager.Broadcast();

            Changed = true;
            MsgWindow.Show(this, "信息", "已应用。\n\n新开的程序会看到新的环境变量；已运行的程序需要重启。", false);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MsgWindow.Show(this, "错误", "应用失败：" + ex.Message, true);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}