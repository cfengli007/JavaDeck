using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JavaDeck.Services;

namespace JavaDeck;

public partial class App : Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    protected override void OnStartup(StartupEventArgs e)
    {
        // WPF 的绑定在 InvariantGlobalization 下列性化会抛
        // "Cannot find non-neutral culture related to 'en-us'"，必须在任何 UI 之前固定区域性。
        try
        {
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("zh-CN");
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("zh-CN");
        }
        catch { }

        // 任何未捕获异常都写日志，不再静默闪退
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => LogFatal(ex.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, ex) =>
        {
            LogFatal(ex.Exception);
            ex.Handled = true;
            try
            {
                var body = ex.Exception.Message + "\n\n" + ex.Exception.StackTrace
                         + "\n\n--- 日志文件 ---\n" + LogPath;
                MsgWindow.Show(Current?.MainWindow, "发生错误", body, true);
            }
            catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            LogFatal(ex.Exception);
            ex.SetObserved();
        };

        if (e.Args.Length > 0 && Cli.IsVerb(e.Args[0]))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            Shutdown(Cli.Run(e.Args));
            return;
        }

        // 允许命令行强制主题：--theme dark|light|system（用于测试与排障）
        try
        {
            var ti = Array.FindIndex(e.Args, a => a.Equals("--theme", StringComparison.OrdinalIgnoreCase));
            if (ti >= 0 && ti + 1 < e.Args.Length)
            {
                var t = e.Args[ti + 1].ToLowerInvariant();
                ThemeManager.Apply(t switch
                {
                    "dark" => AppTheme.Dark,
                    "light" => AppTheme.Light,
                    _ => AppTheme.System,
                });
            }
        }
        catch { }

        if (e.Args.Length > 1 && e.Args[0].Equals("--render", StringComparison.OrdinalIgnoreCase))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            Shutdown(RenderTo(e.Args[1], e.Args.Length > 2 && !e.Args[2].StartsWith("--") ? e.Args[2] : "main"));
            return;
        }

        // 主题要在 MainWindow 创建前应用（StartupUri 机制会在 base.OnStartup 后建窗口）
        try
        {
            var s = Services.AppSettingsEx.Load();
            var ti2 = Array.FindIndex(e.Args, a => a.Equals("--theme", StringComparison.OrdinalIgnoreCase));
            var themeName = ti2 >= 0 && ti2 + 1 < e.Args.Length ? e.Args[ti2 + 1].ToLowerInvariant() : s.Theme;
            Services.ThemeManager.Apply(themeName switch
            {
                "dark" => Services.AppTheme.Dark,
                "light" => Services.AppTheme.Light,
                _ => Services.AppTheme.System,
            });
        }
        catch { }

        // 关键顺序：先应用主题，再创建主窗口。
        // StartupUri 会在 base.OnStartup 里建窗口，所以改用显式创建。
        try
        {
            var s = Services.AppSettingsEx.Load();
            var ti2 = Array.FindIndex(e.Args, a => a.Equals("--theme", StringComparison.OrdinalIgnoreCase));
            var themeName = ti2 >= 0 && ti2 + 1 < e.Args.Length ? e.Args[ti2 + 1].ToLowerInvariant() : s.Theme;
            Services.ThemeManager.Apply(themeName switch
            {
                "dark" => Services.AppTheme.Dark,
                "light" => Services.AppTheme.Light,
                _ => Services.AppTheme.System,
            });
            Services.NetworkSettings.Configure(s);
        }
        catch { }

        // 全局钩子：任何后续创建的窗口（添加 JDK、JAVA_HOME、PATH 排序…）
        // 在 Loaded 时自动套用当前主题（含 DWM 深色框架），不必逐个窗口改代码。
        try
        {
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((s, _) =>
                {
                    if (s is Window w)
                    {
                        try
                        {
                            Services.ThemeManager.ApplyToWindow(w, Services.ThemeManager.IsDarkEffective);
                            if (Application.Current?.TryFindResource("Bg") is System.Windows.Media.Brush bg)
                                w.Background = bg;
                        }
                        catch { }
                    }
                }));
        }
        catch { }

        try
        {
            var dbg = "args=" + string.Join("|", e.Args)
                    + " theme=" + Services.ThemeManager.Current
                    + " dark=" + Services.ThemeManager.IsDarkEffective
                    + " bg=" + (Application.Current?.TryFindResource("Bg") is System.Windows.Media.SolidColorBrush b ? b.Color.ToString() : "null");
            System.IO.File.WriteAllText(@"D:\project-dsh\work\javadeck-shot\theme-debug.txt", dbg);
        }
        catch { }

        base.OnStartup(e);

        // 若 StartupUri 未创建窗口（或需要重新确保），这里兜底
        if (MainWindow == null)
        {
            var w = new MainWindow();
            MainWindow = w;
            w.Show();
        }
    }

    internal static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JavaDeck", "error.log");

    internal static void LogFatal(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private static int RenderTo(string outPath, string which)
    {
        try
        {
            Window win;
            if (which == "add")
            {
                var s = AppSettings.Load();
                var store = new JdkStore(s.RootDir);
                var api = new LibericaClient();
                var queue = new InstallQueue(store, api);
                win = new AddJdkWindow(api, store.LoadAll(), queue, s.RootDir);
            }
            else
            {
                win = new MainWindow();
            }

            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Left = 100;
            win.Top = 100;
            win.Show();
            win.UpdateLayout();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 4000)
            {
                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                System.Threading.Thread.Sleep(40);
                win.UpdateLayout();
            }

            // 用 PrintWindow 抓真实窗口，避免 DPI 逻辑单位问题
            var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            if (!GetWindowRect(hwnd, out var rect))
            {
                Console.WriteLine("GetWindowRect failed");
                return 4;
            }
            var w = rect.Right - rect.Left;
            var h = rect.Bottom - rect.Top;
            Console.WriteLine($"window rect {w}x{h}");

            var hdc = GetDC(IntPtr.Zero);
            var memDc = CreateCompatibleDC(hdc);
            var hBitmap = CreateCompatibleBitmap(hdc, w, h);
            SelectObject(memDc, hBitmap);
            PrintWindow(hwnd, memDc, 2);

            var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var fs = File.Create(outPath)) enc.Save(fs);

            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, hdc);

            Console.WriteLine($"rendered {w}x{h} -> {outPath}");
            win.Close();
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("render failed: " + ex);
            return 3;
        }
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);
}