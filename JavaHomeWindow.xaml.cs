using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JavaDeck.Services;

namespace JavaDeck;

public partial class JavaHomeWindow : Window
{
    private readonly List<InstalledJdk> _all;
    private InstalledJdk? _selected;
    private bool _clear;

    public bool Changed { get; private set; }

    public JavaHomeWindow(List<InstalledJdk> installed)
    {
        InitializeComponent();
        _all = installed.OrderByDescending(j => j.FeatureVersion).ToList();

        var home = EnvManager.GetJdkHome(out var isSystem);
        CurrentHomeText.Text = string.IsNullOrEmpty(home)
            ? "未设置"
            : home + (isSystem ? "   [系统级]" : "   [用户级]");

        _selected = _all.FirstOrDefault(IsCurrentHome);
        Refresh();
    }

    private static bool IsCurrentHome(InstalledJdk jdk)
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

    private void Refresh()
    {
        HomeList.Items.Clear();
        if (_all.Count == 0)
        {
            HomeList.Items.Add(new TextBlock
            {
                Text = "没有已安装的 JDK。先到「JDK → 添加 JDK」安装一个。",
                Foreground = (Brush)FindResource("FgDim"),
                FontSize = 12,
                Margin = new Thickness(10),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var jdk in _all)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

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
            Grid.SetColumn(badge, 0);
            g.Children.Add(badge);

            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            sp.Children.Add(new TextBlock { Text = jdk.Version, FontSize = 12 });
            sp.Children.Add(new TextBlock
            {
                Text = jdk.Path,
                FontSize = 10,
                Foreground = (Brush)FindResource("FgDim"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(sp, 1);
            g.Children.Add(sp);

            var item = new ListBoxItem { Content = g, Tag = jdk, IsSelected = _selected?.Path == jdk.Path };
            HomeList.Items.Add(item);
        }

        HomeList.SelectionChanged += (_, _) =>
        {
            if (HomeList.SelectedItem is ListBoxItem { Tag: InstalledJdk j })
            {
                _selected = j;
                _clear = false;
                HintText.Text = $"将把 Java {j.FeatureVersion}（{j.Version}）设为 JAVA_HOME";
            }
        };
        HintText.Text = _selected != null
            ? $"当前选中：Java {_selected.FeatureVersion}（{_selected.Version}）"
            : "请选择一个 JDK";
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _selected = null;
        _clear = true;
        HintText.Text = "将清空 JAVA_HOME";
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        using var busy = new BusyOverlay((Panel)Content, "正在应用 JAVA_HOME…");
        try
        {
            if (_clear)
            {
                EnvManager.SwitchJavaHome(null);
            }
            else if (_selected != null)
            {
                EnvManager.SwitchJavaHome(_selected.Path);
            }
            else
            {
                MsgWindow.Show(this, "提示", "请先选择一个 JDK。");
                return;
            }

            Changed = true;
            DialogResult = true;   // 环境变量已写入，java -version 探测交给主界面异步做
        }
        catch (Exception ex)
        {
            MsgWindow.Show(this, "应用失败", ex.Message, true);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}