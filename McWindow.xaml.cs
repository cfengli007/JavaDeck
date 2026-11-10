using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JavaDeck.Services;

namespace JavaDeck;

public partial class McWindow : Window
{
    private readonly List<InstalledJdk> _installed;

    public McWindow(List<InstalledJdk> installed)
    {
        InitializeComponent();
        _installed = installed;
    }

    private async void OnFetch(object sender, RoutedEventArgs e)
    {
        FetchBtn.IsEnabled = false;
        Hint.Text = "正在拉取 Mojang 版本清单…";
        Table.Children.Clear();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var api = new MojangApi();
            var progress = new Progress<double>(p => Hint.Text = $"读取版本元数据… {p * 100:F0}%");
            var list = await api.GetReleasesAsync(progress, cts.Token);
            Render(list);
        }
        catch (OperationCanceledException) { Hint.Text = "超时：网络不可达"; }
        catch (Exception ex) { Hint.Text = "获取失败：" + ex.Message; }
        finally { FetchBtn.IsEnabled = true; }
    }

    private void Render(List<McVersion> list)
    {
        var groups = list.Where(v => v.JavaMajor > 0)
                         .GroupBy(v => v.JavaMajor)
                         .OrderBy(g => g.Key)
                         .ToList();
        if (groups.Count == 0) { Hint.Text = "没有取到版本数据"; return; }

        foreach (var g in groups)
        {
            var newest = g.First().Id;
            var oldest = g.Last().Id;
            var have = _installed.Any(j => j.FeatureVersion == g.Key);

            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

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
                Text = "Java " + g.Key,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x4F, 0xB0)),
            };
            Grid.SetColumn(badge, 0);
            row.Children.Add(badge);

            var txt = new TextBlock
            {
                Text = oldest == newest ? oldest : $"{oldest} → {newest}",
                FontSize = 11,
                Foreground = (Brush)FindResource("FgDim"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(txt, 1);
            row.Children.Add(txt);

            var status = new TextBlock
            {
                Text = have ? "✓ 已有" : "缺",
                FontSize = 11,
                Foreground = (Brush)FindResource(have ? "Ok" : "Warn"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            Grid.SetColumn(status, 2);
            row.Children.Add(status);

            Table.Children.Add(row);
        }

        var missing = groups.Count(g => !_installed.Any(j => j.FeatureVersion == g.Key));
        Hint.Text = $"{groups.Count} 个 Java 版本区间";
        Summary.Text = missing == 0 ? "本机已覆盖全部区间" : $"还缺 {missing} 个 Java 版本";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}