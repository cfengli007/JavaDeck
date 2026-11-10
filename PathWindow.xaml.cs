using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JavaDeck.Services;

namespace JavaDeck;

/// <summary>
/// 只管理 JDK 在 PATH 中的相对顺序。
/// 保存时：把"JDK 那几条"的原始槽位提取出来，按新顺序回填，
/// 非 JDK 条目位置绝对不动。
/// </summary>
public partial class PathWindow : Window
{
    private readonly List<string> _allEntries;   // 完整 PATH，含非 JDK
    private readonly List<int> _jdkSlots;        // JDK 条目在 PATH 中的原始下标
    private List<string> _jdkEntries;            // 仅 JDK，可排序

    public PathWindow(List<InstalledJdk> installed)
    {
        InitializeComponent();

        var raw = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        _allEntries = raw.Split(';', StringSplitOptions.RemoveEmptyEntries)
                         .Select(p => p.Trim())
                         .Where(p => p.Length > 0)
                         .ToList();

        _jdkSlots = new List<int>();
        _jdkEntries = new List<string>();

        // JAVA_HOME 不参与 PATH 排序：它自己的 bin 不列出来
        var home = EnvManager.GetJdkHome(out _);
        var homeBin = string.IsNullOrEmpty(home) ? null : Path.Combine(home, "bin");

        for (var i = 0; i < _allEntries.Count; i++)
        {
            var e = _allEntries[i];
            if (!IsJdkEntry(e, installed)) continue;
            if (homeBin != null && string.Equals(
                    Environment.ExpandEnvironmentVariables(e).TrimEnd('\\','/'),
                    homeBin.TrimEnd('\\','/'), StringComparison.OrdinalIgnoreCase)) continue;
            if (e.Contains("%JAVA_HOME%", StringComparison.OrdinalIgnoreCase)) continue;

            _jdkSlots.Add(i);
            _jdkEntries.Add(e);
        }

        ScopeText.Text = _jdkEntries.Count == 0
            ? "当前 PATH 中没有 JDK 条目"
            : $"PATH 共 {_allEntries.Count} 条，其中 JDK {_jdkEntries.Count} 条。保存时只重排这 {_jdkEntries.Count} 条占据的槽位，其余 {_allEntries.Count - _jdkEntries.Count} 条位置不变。";

        Refresh();
    }

    private static bool IsJdkEntry(string entry, List<InstalledJdk> installed)
    {
        // %JAVA_HOME%\bin 也算
        if (entry.Replace("%JAVA_HOME%", "").Length == 0 ||
            entry.Contains("%JAVA_HOME%", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(entry).TrimEnd('\\', '/');
            if (!expanded.EndsWith("bin", StringComparison.OrdinalIgnoreCase)) return false;

            // 精确匹配已登记的 JDK
            foreach (var j in installed)
            {
                var jbin = Path.Combine(j.Path, "bin").TrimEnd('\\', '/');
                if (string.Equals(jbin, expanded, StringComparison.OrdinalIgnoreCase)) return true;
            }

            // 兜底：按命名判断（应对 JDK 已被删但 PATH 还留着）
            var parent = Directory.GetParent(expanded)?.Name ?? "";
            return parent.Contains("jdk", StringComparison.OrdinalIgnoreCase)
                || parent.Contains("jre", StringComparison.OrdinalIgnoreCase)
                || parent.Contains("liberica", StringComparison.OrdinalIgnoreCase)
                || parent.Contains("java", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private void Refresh()
    {
        JdkPathList.Items.Clear();
        if (_jdkEntries.Count == 0)
        {
            JdkPathList.Items.Add(new TextBlock
            {
                Text = "PATH 里没有 JDK 条目。先在主界面「设为 JAVA_HOME」或「JDK 管理」里添加。",
                Foreground = (Brush)FindResource("FgDim"),
                FontSize = 12,
                Margin = new Thickness(10),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        var idx = 1;
        foreach (var e in _jdkEntries)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var num = new TextBlock
            {
                Text = "#" + idx,
                FontSize = 11,
                Foreground = (Brush)FindResource("FgFaint"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(num, 0);
            g.Children.Add(num);

            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var isHome = e.Contains("%JAVA_HOME%", StringComparison.OrdinalIgnoreCase);
            sp.Children.Add(new TextBlock
            {
                Text = e,
                FontSize = 12,
                Foreground = (Brush)FindResource("Fg"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (isHome)
                sp.Children.Add(new TextBlock { Text = "当前 JAVA_HOME", FontSize = 10, Foreground = (Brush)FindResource("Ok") });
            Grid.SetColumn(sp, 1);
            g.Children.Add(sp);

            JdkPathList.Items.Add(new ListBoxItem { Content = g, Tag = e });
            idx++;
        }
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);
    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(+1);

    private void Move(int delta)
    {
        if (JdkPathList.SelectedItem is not ListBoxItem { Tag: string sel }) return;
        var idx = _jdkEntries.IndexOf(sel);
        if (idx < 0) return;
        var nidx = Math.Clamp(idx + delta, 0, _jdkEntries.Count - 1);
        if (nidx == idx) return;
        _jdkEntries.RemoveAt(idx);
        _jdkEntries.Insert(nidx, sel);
        Refresh();
        JdkPathList.SelectedIndex = nidx;
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (JdkPathList.SelectedItem is not ListBoxItem { Tag: string sel }) return;
        if (!MsgWindow.Confirm(this, "确认", $"从 PATH 中移除这条？\n\n{sel}\n\n不会删除 JDK 文件。")) return;

        var slotIdx = _jdkSlots[_jdkEntries.IndexOf(sel)];
        _jdkSlots.Remove(slotIdx);
        _jdkEntries.Remove(sel);
        // 重新计算剩余槽位（因为删了一个下标）
        for (var i = 0; i < _jdkSlots.Count; i++)
            if (_jdkSlots[i] > slotIdx) _jdkSlots[i]--;
        Refresh();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        using var busy = new BusyOverlay((Panel)Content, "正在保存 PATH 顺序…");
        try
        {
            // 把非 JDK 条目按原位放好，JDK 条目按新顺序填进它们原来的槽位
            var result = new List<string>(_allEntries);

            // 先移除所有 JDK 条目（从后往前删，避免下标漂移）
            foreach (var slot in _jdkSlots.OrderByDescending(s => s))
                if (slot >= 0 && slot < result.Count) result.RemoveAt(slot);

            // 再按新顺序插回这些槽位（从小到大插）
            for (var i = 0; i < _jdkSlots.Count && i < _jdkEntries.Count; i++)
            {
                var slot = Math.Min(_jdkSlots[i], result.Count);
                result.Insert(slot, _jdkEntries[i]);
            }

            // 如果 JDK 比槽位多（理论上不会），追加到末尾
            for (var i = _jdkSlots.Count; i < _jdkEntries.Count; i++)
                result.Add(_jdkEntries[i]);

            Environment.SetEnvironmentVariable("PATH", string.Join(";", result), EnvironmentVariableTarget.User);
            EnvManager.Broadcast();
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MsgWindow.Show(this, "错误", "保存失败：" + ex.Message, true);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}