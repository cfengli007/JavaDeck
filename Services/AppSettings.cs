using System.IO;
using System.Text.Json;

namespace JavaDeck.Services;

public sealed class AppSettings
{
    /// <summary>JDK 安装根目录。默认放在用户目录下，不需要管理员权限。</summary>
    public string RootDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Java");

    public string MirrorBase { get; set; } = "https://download.bell-sw.com/java/";
    public bool UseMirror { get; set; }

    private static string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JavaDeck", "settings.json");

    public static AppSettings Load()
    {
        var overridden = Environment.GetEnvironmentVariable("JAVADECK_ROOT");
        AppSettings cfg = new();
        try
        {
            if (File.Exists(ConfigPath))
            {
                var j = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(ConfigPath));
                if (j != null && !string.IsNullOrWhiteSpace(j.RootDir)) cfg = j;
            }
        }
        catch { }
        if (!string.IsNullOrWhiteSpace(overridden)) cfg.RootDir = overridden;
        return cfg;
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
