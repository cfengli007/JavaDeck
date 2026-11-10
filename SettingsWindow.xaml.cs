using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JavaDeck.Services;

namespace JavaDeck;

public partial class SettingsWindow : Window
{
    private readonly AppSettingsEx _settings;

    public bool Changed { get; private set; }

    public SettingsWindow(AppSettingsEx settings)
    {
        InitializeComponent();
        _settings = settings;

        Loaded += (_, _) =>
        {
            ThemeManager.ApplyToWindowDeferred(this);
            WindowEffects.Apply(this);
            ThemeManager.ApplyToWindow(this, ThemeManager.IsDarkEffective);
        };

        Load();
    }

    private void Load()
    {
        // 主题
        switch (_settings.Theme)
        {
            case "light": ThemeLight.IsChecked = true; break;
            case "dark": ThemeDark.IsChecked = true; break;
            default: ThemeSystem.IsChecked = true; break;
        }

        // 启动
        ChkAutoStart.IsChecked = AutoStart.IsEnabled();
        ChkMinimized.IsChecked = _settings.StartMinimizedToTray;
        ChkCloseToTray.IsChecked = _settings.CloseToTray;

        // 代理
        ChkProxy.IsChecked = _settings.UseProxy;
        TxtProxy.Text = _settings.ProxyAddress;
        ChkBypassLocal.IsChecked = _settings.BypassProxyOnLocal;
        UpdateProxyEnabled();

        // 下载
        var seg = Math.Clamp(_settings.DownloadSegments, 1, 12);
        CmbSegments.SelectedIndex = seg switch { 1 => 0, <= 4 => 1, <= 6 => 2, <= 8 => 3, _ => 4 };

        // 行为
        ChkAutoDeleteOld.IsChecked = _settings.AutoDeleteOldVersion;
    }

    private void UpdateProxyEnabled()
    {
        var on = ChkProxy.IsChecked == true;
        TxtProxy.IsEnabled = on;
        BtnTestProxy.IsEnabled = on;
        ChkBypassLocal.IsEnabled = on;
    }

    private void OnProxyToggled(object sender, RoutedEventArgs e) => UpdateProxyEnabled();

    private void OnThemeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var theme = ThemeDark.IsChecked == true ? AppTheme.Dark
                  : ThemeLight.IsChecked == true ? AppTheme.Light
                  : AppTheme.System;
        ThemeManager.Apply(theme);
        // 主题切换后当前窗口背景要跟着变
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("Bg");
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        // 立即生效，用户不用等保存
        var ok = AutoStart.Enable(ChkAutoStart.IsChecked == true,
                                  ChkMinimized.IsChecked == true);
        HintText.Text = ok
            ? (ChkAutoStart.IsChecked == true ? "已开启开机自启" : "已关闭开机自启")
            : "写入自启项失败（可能需要权限）";
    }

    private async void OnTestProxy(object sender, RoutedEventArgs e)
    {
        var addr = TxtProxy.Text.Trim();
        if (string.IsNullOrEmpty(addr)) { HintText.Text = "请先填写代理地址"; return; }

        BtnTestProxy.IsEnabled = false;
        HintText.Text = "正在测试 " + addr + " …";
        try
        {
            var ok = await Task.Run(() =>
            {
                try
                {
                    var handler = new System.Net.Http.HttpClientHandler { Proxy = new System.Net.WebProxy(addr), UseProxy = true };
                    using var http = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                    using var resp = http.GetAsync("https://api.bell-sw.com/v1/liberica/operating-systems").GetAwaiter().GetResult();
                    return resp.IsSuccessStatusCode;
                }
                catch { return false; }
            });

            HintText.Text = ok ? "代理可用 ✓（已能访问 BellSoft API）" : "代理连接失败";
        }
        finally { BtnTestProxy.IsEnabled = true; }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _settings.Theme = ThemeDark.IsChecked == true ? "dark"
                        : ThemeLight.IsChecked == true ? "light" : "system";
        _settings.StartMinimizedToTray = ChkMinimized.IsChecked == true;
        _settings.CloseToTray = ChkCloseToTray.IsChecked == true;

        _settings.UseProxy = ChkProxy.IsChecked == true;
        _settings.ProxyAddress = TxtProxy.Text.Trim();
        _settings.BypassProxyOnLocal = ChkBypassLocal.IsChecked == true;

        _settings.DownloadSegments = CmbSegments.SelectedIndex switch
        { 0 => 1, 1 => 4, 2 => 6, 3 => 8, 4 => 12, _ => 6 };

        _settings.AutoDeleteOldVersion = ChkAutoDeleteOld.IsChecked == true;

        // 自启状态以注册表为准
        _settings.StartWithWindows = AutoStart.IsEnabled();

        Changed = true;
        if (!_settings.Save())
        {
            HintText.Text = "设置保存失败：" + (AppSettingsEx.SaveError ?? "权限不足");
            return;
        }

        ToastSink.Apply(_settings);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } }
    }
}

/// <summary>设置变更后需要即时生效的部分（代理、并行数等）。</summary>
public static class ToastSink
{
    public static void Apply(AppSettingsEx s)
    {
        NetworkSettings.Configure(s);
    }
}
