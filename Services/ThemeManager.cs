using System.Windows;

namespace JavaDeck.Services;

public enum AppTheme { Light, Dark, System }

/// <summary>
/// 主题切换。
///
/// 深色窗口的关键难点（查过 MS Learn 后的结论）：
///   1. WPF 的 Window 在 WindowStyle=None 时，非客户区由 DWM 绘制 ——
///      必须用 DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE, TRUE) 让 DWM 画深色框架
///   2. 窗口背景必须自己设成深色，否则内容渲染前的瞬间是白底（闪白）
///   3. 标题栏、边框、菜单都是 WPF 自绘的，用 DynamicResource 换色即可
///   4. DWMWA_BORDER_COLOR 在 Win11 22000+ 生效，用于消除浅色描边
/// </summary>
public static class ThemeManager
{
    // 必须用绝对 pack URI。相对 URI 在运行时会被解析成
    // "themes/dark.xaml"（小写、无程序集前缀），PackWebRequest 找不到对应的 baml。
    private static readonly Uri LightUri =
        new("pack://application:,,,/JavaDeck;component/Themes/Light.xaml", UriKind.Absolute);
    private static readonly Uri DarkUri =
        new("pack://application:,,,/JavaDeck;component/Themes/Dark.xaml", UriKind.Absolute);

    public static AppTheme Current { get; private set; } = AppTheme.System;

    public static bool IsDarkEffective => Current switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => SystemPrefersDark(),
    };

    private static bool SystemPrefersDark()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var v = k?.GetValue("AppsUseLightTheme");
            return v is int i && i == 0;
        }
        catch { return false; }
    }

    /// <summary>切换主题并应用到全部已打开窗口。</summary>
    public static void Apply(AppTheme theme)
    {
        Current = theme;
        var dark = IsDarkEffective;

        var app = Application.Current;
        if (app == null || app.Resources == null) return;

        var dicts = app.Resources.MergedDictionaries;
        var uri = dark ? DarkUri : LightUri;

        // 关键：必须原地替换"同一个 ResourceDictionary 实例"的内容，
        // 而不是把 MergedDictionaries 里的元素换成新对象。
        // DynamicResource 的失效通知依赖字典实例本身，换实例不会触发已绑定属性重新求值。
        ResourceDictionary? target = null;
        for (var i = 0; i < dicts.Count; i++)
        {
            var src = dicts[i]?.Source?.OriginalString ?? "";
            if (src.Contains("Themes/Light.xaml") || src.Contains("Themes/Dark.xaml")) { target = dicts[i]; break; }
        }

        if (target == null)
        {
            target = new ResourceDictionary { Source = uri };
            dicts.Insert(0, target);
        }
        else
        {
            if (target.Source != uri)
            {
                target.Source = uri;   // 同一实例换 Source，会触发集合级失效
            }
        }

        // 双保险：显式把所有窗口的背景/前景重设一次
        foreach (Window w in app.Windows)
        {
            if (app.TryFindResource("Bg") is System.Windows.Media.Brush bg) w.Background = bg;
            if (app.TryFindResource("Fg") is System.Windows.Media.Brush fg) w.Foreground = fg;
            ApplyToWindow(w, dark);
        }
    }

    /// <summary>
    /// 对单个窗口应用深色/浅色框架。
    /// 注意：只设 Window.Background 不够——子窗口里 Border/Grid 用的是 DynamicResource，
    /// 它们在 Show() 时就已求值成当时（浅色）的画笔。必须遍历可视化树，
    /// 把当前主题的画笔重新推给这些元素。
    /// </summary>
    public static void ApplyToWindow(Window window, bool dark)
    {
        try
        {
            if (Application.Current?.TryFindResource("Bg") is System.Windows.Media.Brush bg)
                window.Background = bg;
            if (Application.Current?.TryFindResource("Fg") is System.Windows.Media.Brush fg)
                window.Foreground = fg;

            // 把主题画笔重新推给树里所有用这些资源名的元素
            ReassertBrushes(window);

            // 2) DWM 深色框架（覆盖非客户区、圆角外框、阴影）
            WindowEffects.SetDarkMode(window, dark);

            // 3) 边框颜色跟随主题
            WindowEffects.SetBorderColor(window, dark ? 0x00_1C17_15 : 0x00EBE5E2);

            // 4) Win11 标题栏配色（仅对原生标题栏有效，我们用自绘标题栏，设了也无害）
            WindowEffects.SetCaptionColor(window, dark ? 0x00_1C17_15 : 0x00F3F4F7);
        }
        catch { }
    }

    /// <summary>窗口句柄创建后调用，确保 DWM 设置落在正确的 HWND 上。</summary>
    public static void ApplyToWindowDeferred(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            ApplyToWindow(window, IsDarkEffective);
        };
    }

    /// <summary>
    /// 遍历可视化树，把主题背景色重新推向当前主题。
    /// DynamicResource 的元素首次创建时求值；字典替换后已创建的元素在某些时机不会重算，
    /// 尤其是 Show() 过程中。这里按"当前值是否属于已知主题背景色"来判断并重设。
    /// </summary>
    private static void ReassertBrushes(DependencyObject root)
    {
        try
        {
            if (root is System.Windows.Controls.Border b && b.Background is System.Windows.Media.SolidColorBrush bb
                && IsThemeBackgroundColor(bb.Color))
            {
                if (Application.Current?.TryFindResource("Bg") is System.Windows.Media.Brush nb)
                    b.Background = nb;
            }
            else if (root is System.Windows.Controls.Panel pn && pn.Background is System.Windows.Media.SolidColorBrush pb
                     && IsThemeBackgroundColor(pb.Color))
            {
                if (Application.Current?.TryFindResource("Bg") is System.Windows.Media.Brush nb2)
                    pn.Background = nb2;
            }

            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
                ReassertBrushes(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
        }
        catch { }
    }

    private static readonly System.Windows.Media.Color[] KnownBgs =
    {
        System.Windows.Media.Color.FromRgb(0xF3, 0xF4, 0xF7),
        System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF),
        System.Windows.Media.Color.FromRgb(0xFA, 0xFB, 0xFC),
        System.Windows.Media.Color.FromRgb(0x15, 0x17, 0x1C),
        System.Windows.Media.Color.FromRgb(0x1B, 0x1E, 0x25),
        System.Windows.Media.Color.FromRgb(0x21, 0x25, 0x2E),
    };

    private static bool IsThemeBackgroundColor(System.Windows.Media.Color c)
    {
        foreach (var k in KnownBgs) if (k == c) return true;
        return false;
    }
}
