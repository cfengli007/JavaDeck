using System.IO;
using System.Text.Json;

namespace JavaDeck.Services;

/// <summary>扩展设置：主题、开机自启、代理、下载并发等。</summary>
public sealed class AppSettingsEx
{
    // ---- 外观 ----
    public string Theme { get; set; } = "system";   // light / dark / system

    // ---- 启动 ----
    public bool StartWithWindows { get; set; }
    public bool StartMinimizedToTray { get; set; }

    // ---- 网络 ----
    public bool UseProxy { get; set; }
    public string ProxyAddress { get; set; } = "";   // 如 http://127.0.0.1:7890
    public bool BypassProxyOnLocal { get; set; } = true;

    // ---- 下载 ----
    public int DownloadSegments { get; set; } = 6;   // 并行分段数
    public int MaxParallelDownloads { get; set; } = 3;

    // ---- 行为 ----
    public bool CloseToTray { get; set; } = true;
    public bool AutoDeleteOldVersion { get; set; } = true;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JavaDeck", "app.json");

    public static AppSettingsEx Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettingsEx>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public bool Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
            SaveError = null;
            return true;
        }
        catch (Exception ex) { SaveError = ex.Message; return false; }
    }

    public static string? SaveError { get; private set; }
}
