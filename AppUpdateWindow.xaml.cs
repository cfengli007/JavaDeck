using System.Diagnostics;
using System.Windows;
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
        VersionText.Text = $"当前版本 {SelfUpdater.CurrentVersion} · {SelfUpdater.RepoUrl}";
        Loaded += async (_, _) =>
        {
            ThemeManager.ApplyToWindowDeferred(this);
            await CheckAsync();
        };
    }

    private async Task CheckAsync()
    {
        if (_busy) return;
        _busy = true;
        ActionButton.IsEnabled = false;
        StatusTitle.Text = "正在检查…";
        StatusDetail.Text = "连接 GitHub Releases";
        ReleaseNotes.Text = "";

        try
        {
            _result = await SelfUpdater.CheckAsync();

            if (_result.Error != null)
            {
                StatusTitle.Text = "检查失败";
                StatusDetail.Text = _result.Error;
                ActionButton.Content = "重试";
                return;
            }

            if (_result.Release == null) { StatusTitle.Text = "没有可用版本"; return; }

            StatusDetail.Text =
                $"最新版本 {_result.LatestVersion} · 发布于 {_result.Release.PublishedAt:yyyy-MM-dd HH:mm}";
            ReleaseNotes.Text = string.IsNullOrWhiteSpace(_result.Release.Body)
                ? "（无更新说明）"
                : _result.Release.Body;

            if (_result.HasUpdate)
            {
                StatusTitle.Text = $"发现新版本 {_result.LatestVersion}";
                ActionButton.Content = _result.Installer != null
                    ? "下载并安装"
                    : "打开发布页";
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
        if (_result == null || _busy) { await CheckAsync(); return; }

        // 没有安装包或已是最新 → 打开发布页
        if (!_result.HasUpdate || _result.Installer == null)
        {
            if (_result.Release != null) OpenUrl(_result.Release.HtmlUrl);
            else await CheckAsync();
            return;
        }

        // 下载
        _busy = true;
        ActionButton.IsEnabled = false;
        Bar.Visibility = Visibility.Visible;
        StatusTitle.Text = "正在下载 " + _result.Installer.Name;

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
            StatusDetail.Text = _downloadedFile;
            ActionButton.Content = "运行安装程序";

            // 只有管理员才能装到 Program Files；这里拉起 UAC
            var r = MsgWindow.Confirm(this, "安装更新",
                $"安装包已下载到：\n{_downloadedFile}\n\n" +
                "点击「确定」将启动安装程序（会请求管理员权限）。\n" +
                "安装程序会自动关闭正在运行的 JavaDeck。");

            if (r) RunInstaller(_downloadedFile);
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

    private static void RunInstaller(string file)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file)
            {
                UseShellExecute = true,
                Verb = "runas",     // 请求管理员
            });
        }
        catch (Exception ex)
        {
            MsgWindow.Show(null, "启动失败", ex.Message, true);
        }
    }

    private void OnOpenRepo(object sender, RoutedEventArgs e) => OpenUrl(SelfUpdater.RepoUrl);

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
