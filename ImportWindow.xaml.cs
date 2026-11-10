using System.IO;
using System.Windows;
using System.Windows.Controls;
using JavaDeck.Services;

namespace JavaDeck;

public partial class ImportWindow : Window
{
    private readonly List<InstalledJdk> _installed;
    private readonly JdkStore _store;
    public List<InstalledJdk> Imported { get; } = new();

    public ImportWindow(List<InstalledJdk> installed, JdkStore store)
    {
        InitializeComponent();
        _installed = installed;
        _store = store;
        Loaded += (_, _) => Rescan();
    }

    private void Rescan()
    {
        ScanHint.Text = "正在扫描…";
        HitList.Items.Clear();
        try
        {
            var hits = JdkScanner.Scan(_installed.Select(i => i.Path));
            var fresh = hits.Where(h => !h.AlreadyManaged).ToList();

            foreach (var h in fresh)
            {
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var badge = new Border
                {
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xF0, 0xFE)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(7, 2, 7, 2),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                badge.Child = new TextBlock { Text = "Java " + h.Feature, FontSize = 11, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1F, 0x4F, 0xB0)) };
                Grid.SetColumn(badge, 0);
                g.Children.Add(badge);

                var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = h.Version, FontSize = 12 });
                sp.Children.Add(new TextBlock { Text = h.Path, FontSize = 10, Foreground = (System.Windows.Media.Brush)FindResource("FgDim"), TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(sp, 1);
                g.Children.Add(sp);

                var src = new TextBlock { Text = h.Source, FontSize = 10, Foreground = (System.Windows.Media.Brush)FindResource("FgDim"), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(src, 2);
                g.Children.Add(src);

                var item = new ListBoxItem { Content = g, Tag = h };
                HitList.Items.Add(item);
            }

            ScanHint.Text = fresh.Count == 0
                ? "没有发现新的 JDK（已登记的不再显示）"
                : $"发现 {fresh.Count} 个未登记的 JDK，可多选";
        }
        catch (Exception ex) { ScanHint.Text = "扫描失败：" + ex.Message; }
    }

    private void OnRescan(object sender, RoutedEventArgs e) => Rescan();

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择 JDK 根目录（含 bin\\java.exe）" };
        if (dlg.ShowDialog() != true) return;
        var dir = dlg.FolderName;
        if (!File.Exists(Path.Combine(dir, "bin", "java.exe")))
        {
            MsgWindow.Show(this, "提示", "该目录下没有 bin\\java.exe，不是 JDK 根目录。", false);
            return;
        }
        var feature = JdkScanner.ReadFeature(dir);
        var version = JdkScanner.ReadVersion(dir) ?? Path.GetFileName(dir);
        var hit = new ScanHit { Path = dir, Feature = feature, Version = version, Source = "手动" };

        var g = new StackPanel();
        g.Children.Add(new TextBlock { Text = $"Java {feature}  {version}", FontSize = 12 });
        g.Children.Add(new TextBlock { Text = dir, FontSize = 10, Foreground = (System.Windows.Media.Brush)FindResource("FgDim") });
        var item = new ListBoxItem { Content = g, Tag = hit, IsSelected = true };
        HitList.Items.Add(item);
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        foreach (var item in HitList.SelectedItems)
        {
            if (item is not ListBoxItem { Tag: ScanHit hit }) continue;
            if (Imported.Any(i => string.Equals(i.Path, hit.Path, StringComparison.OrdinalIgnoreCase))) continue;
            Imported.Add(new InstalledJdk
            {
                Id = "import-" + hit.Feature + "-" + Path.GetFileName(hit.Path.TrimEnd('\\')),
                FeatureVersion = hit.Feature,
                Version = hit.Version,
                Path = hit.Path,
                SizeBytes = JdkStore.DirSize(hit.Path),
                InstalledAt = DateTime.Now,
            });
        }
        if (Imported.Count == 0) { MsgWindow.Show(this, "信息", "请先选中要导入的 JDK", false); return; }
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}