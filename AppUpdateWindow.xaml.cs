using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using JavaDeck.Services;

namespace JavaDeck;

public partial class AppUpdateWindow : Window
{
    private UpdateCheckResult? _result;
    private string? _downloadedFile;
    private bool _busy;

    public AppUpdateWindow()
    {
        InitializeComponent();

        CurrentVersionText.Text = SelfUpdater.CurrentVersion;
        RepoText.Text = SelfUpdater.RepoUrl;
        LatestVersionText.Text = "—";

        Loaded += async (_, _) =>
        {
            ThemeManager.ApplyToWindowDeferred(this);
            WindowEffects.Apply(this);
            ThemeManager.ApplyToWindow(this, ThemeManager.IsDarkEffective);
            await CheckAsync();
        };
    }

    private async Task CheckAsync()
    {
        if (_busy) return;
        _busy = true;
        ActionButton.IsEnabled = false;
        ActionButton.Content = "检查中";
        StatusTitle.Text = "正在检查…";
        StatusDetail.Text = "连接 GitHub Releases";
        ReleaseNotes.Text = "（检查后显示）";
        Bar.Visibility = Visibility.Collapsed;
        HintText.Text = "";

        try
        {
            _result = await SelfUpdater.CheckAsync();

            if (_result.Error != null)
            {
                StatusTitle.Text = "检查失败";
                StatusDetail.Text = _result.Error;
                ActionButton.Content = "重试";
                ActionButton.IsEnabled = true;
                HintText.Text = "可稍后重试，或到项目页面手动下载";
                return;
            }

            if (_result.Release == null)
            {
                StatusTitle.Text = "没有可用版本";
                ActionButton.Content = "重新检查";
                return;
            }

            LatestVersionText.Text = _result.LatestVersion;
            var pub = _result.Release.PublishedAt.HasValue
                ? $"发布于 {_result.Release.PublishedAt:yyyy-MM-dd HH:mm}"
                : _result.Release.Name;
            StatusDetail.Text = pub;

            ReleaseNotes.Text = string.IsNullOrWhiteSpace(_result.Release.Body)
                ? "（这个版本没有写更新说明）"
                : _result.Release.Body;

            if (_result.HasUpdate)
            {
                StatusTitle.Text = $"发现新版本 {_result.LatestVersion}";
                ActionButton.Content = _result.Installer != null ? "下载并安装" : "打开发布页";
            }
            else
            {
                StatusTitle.Text = "已是最新版本";
                ActionButton.Content = "重新检查";
            }
        }
        finally
        {
            _busy = false;
            ActionButton.IsEnabled = true;
        }
    }

    private async void OnAction(object sender, RoutedEventArgs e)
    {
        // 下载完成后按钮变成「运行安装程序」
        if (ActionButton.Tag as string == "run") { OnActionRun(); return; }

        if (_result == null || _busy) { await CheckAsync(); return; }

        if (!_result.HasUpdate || _result.Installer == null)
        {
            if (_result.Release != null) OpenUrl(_result.Release.HtmlUrl);
            else await CheckAsync();
            return;
        }

        _busy = true;
        ActionButton.IsEnabled = false;
        Bar.Visibility = Visibility.Visible;
        StatusTitle.Text = "正在下载安装包";
        HintText.Text = _result.Installer.Name;

        try
        {
            var progress = new Progress<(long done, long total)>(p =>
            {
                var pct = p.total > 0 ? p.done * 100.0 / p.total : 0;
                Bar.Value = pct;
                StatusDetail.Text = $"{p.done / 1048576.0:F1} / {p.total / 1048576.0:F1} MB";
            });

            _downloadedFile = await SelfUpdater.DownloadInstallerAsync(_result.Installer, progress, default);

            StatusTitle.Text = "下载完成";
            StatusDetail.Text = "点击「运行安装程序」开始升级";
            HintText.Text = _downloadedFile;
            ActionButton.Content = "运行安装程序";
            ActionButton.Tag = "run";

            _busy = false;
            ActionButton.IsEnabled = true;
            return;
        }
        catch (OperationCanceledException) { StatusTitle.Text = "下载已取消"; }
        catch (Exception ex)
        {
            StatusTitle.Text = "下载失败";
            StatusDetail.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            ActionButton.IsEnabled = true;
            Bar.Visibility = Visibility.Collapsed;
        }
    }

    // 下载完成后，同一按钮变成「运行安装程序」
    private void OnActionRun()
    {
        if (_downloadedFile == null) return;
        if (!MsgWindow.Confirm(this, "安装更新",
                $"安装包已下载：\n{_downloadedFile}\n\n" +
                "点击「确定」将启动安装程序（会请求管理员权限），\n" +
                "它会自动关闭正在运行的 JavaDeck。"))
            return;
        RunInstaller(_downloadedFile);
    }

    private static void RunInstaller(string file)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, Verb = "runas" });
        }
        catch (Exception ex)
        {
            MsgWindow.Show(null, "启动失败", ex.Message, true);
        }
    }

    private void OnOpenRepo(object sender, MouseButtonEventArgs e) => OpenUrl(SelfUpdater.RepoUrl);

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