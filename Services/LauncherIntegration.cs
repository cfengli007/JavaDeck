using System.Diagnostics;
using System.IO;

namespace JavaDeck.Services;

/// <summary>探测本机 Minecraft 启动器。只查极少数固定路径，全程不做递归枚举，避免拖慢启动。</summary>
public static class LauncherIntegration
{
    public sealed record LauncherInfo(string Name, string? ExePath, string? GameDir);

    public static string DefaultGameDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

    /// <summary>启动器可能的固定落点（目录内直接放 exe，或下一层）。</summary>
    private static IEnumerable<(string name, string dir, string exe)> Candidates()
    {
        var roam = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        yield return ("HMCL", Path.Combine(user, ".hmcl"), "HMCL.exe");
        yield return ("Prism", Path.Combine(roam, "PrismLauncher"), "prismlauncher.exe");

        foreach (var d in new[] { @"D:\MC\PCL", @"C:\MC\PCL", @"D:\PCL", @"E:\MC\PCL", @"D:\Minecraft\PCL" })
            yield return ("PCL2", d, "Plain Craft Launcher 2.exe");

        foreach (var d in new[] { @"D:\MC\HMCL", @"D:\HMCL", @"C:\HMCL", @"E:\HMCL" })
            yield return ("HMCL", d, "HMCL.exe");

        foreach (var d in new[] { @"D:\MC\PrismLauncher", @"D:\PrismLauncher" })
            yield return ("Prism", d, "prismlauncher.exe");

        foreach (var d in new[] { @"D:\MC\BakaXL", @"D:\BakaXL" })
            yield return ("BakaXL", d, "BakaXL.exe");
    }

    public static List<LauncherInfo> Detect()
    {
        var found = new List<LauncherInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, dir, exe) in Candidates())
        {
            if (!seen.Add(name)) continue;
            try
            {
                if (!Directory.Exists(dir)) continue;
                var path = Path.Combine(dir, exe);
                if (!File.Exists(path))
                {
                    // 只多看一层：目录下的子目录里找同名 exe
                    path = OneLevelDown(dir, exe);
                    if (path == null) continue;
                }
                found.Add(new LauncherInfo(name, path, GuessGameDir(Path.GetDirectoryName(path)!)));
            }
            catch { }
        }

        try
        {
            var gameDir = DefaultGameDir;
            if (Directory.Exists(gameDir))
                found.Add(new LauncherInfo(".minecraft", null, gameDir));
        }
        catch { }

        return found;
    }

    private static string? OneLevelDown(string dir, string exe)
    {
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var p = Path.Combine(sub, exe);
                if (File.Exists(p)) return p;
            }
        }
        catch { }
        return null;
    }

    private static string GuessGameDir(string launcherDir)
    {
        try
        {
            var nested = Path.Combine(launcherDir, ".minecraft");
            if (Directory.Exists(nested)) return nested;
            var parent = Path.GetDirectoryName(launcherDir);
            if (parent != null)
            {
                var pn = Path.Combine(parent, ".minecraft");
                if (Directory.Exists(pn)) return pn;
            }
        }
        catch { }
        return DefaultGameDir;
    }

    public static List<string> InstalledGameVersions(string? gameDir = null)
    {
        var dir = Path.Combine(gameDir ?? DefaultGameDir, "versions");
        try
        {
            if (!Directory.Exists(dir)) return new List<string>();
            return Directory.GetDirectories(dir)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return new List<string>(); }
    }
}