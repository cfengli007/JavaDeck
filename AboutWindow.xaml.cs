using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using JavaDeck.Services;

namespace JavaDeck;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        var v = SelfUpdater.CurrentVersion;
        VersionText.Text = $"版本 {v} · Windows 原生 JDK 版本管理器";
        RepoText.Text = SelfUpdater.RepoUrl;

        Loaded += (_, _) =>
        {
            ThemeManager.ApplyToWindowDeferred(this);
            WindowEffects.Apply(this);
            ThemeManager.ApplyToWindow(this, ThemeManager.IsDarkEffective);
        };
    }

    private void OnViewLicense(object sender, RoutedEventArgs e)
    {
        // 优先读程序目录下的 LICENSE；没有就打开发布页
        var local = System.IO.Path.Combine(AppContext.BaseDirectory, "LICENSE");
        if (System.IO.File.Exists(local))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = "\"" + local + "\"",
                    UseShellExecute = true,
                });
                return;
            }
            catch { }
        }
        OpenUrl(SelfUpdater.RepoUrl + "/blob/main/LICENSE");
    }

    private void OnOpenRepo(object sender, RoutedEventArgs e) => OpenUrl(SelfUpdater.RepoUrl);

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
