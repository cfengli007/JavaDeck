using System.IO;

namespace JavaDeck.Services;

public sealed class ScanHit
{
    public string Path { get; set; } = "";
    public int Feature { get; set; }
    public string Version { get; set; } = "";
    public string Source { get; set; } = "";
    public bool AlreadyManaged { get; set; }
}

/// <summary>扫描本机已有的 JDK。只查已知目录，不做全盘递归。</summary>
public static class JdkScanner
{
    public static List<ScanHit> Scan(IEnumerable<string> alreadyManaged, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var managed = new HashSet<string>(alreadyManaged.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        var hits = new List<ScanHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (root, label, depth) in Roots())
        {
            ct.ThrowIfCancellationRequested();
            try { if (!Directory.Exists(root)) continue; } catch { continue; }

            progress?.Report("扫描 " + root);

            foreach (var dir in Expand(root, depth, ct))
            {
                var home = ResolveJdkHome(dir);
                if (home == null) continue;
                var normalized = Normalize(home);
                if (!seen.Add(normalized)) continue;

                var feature = JdkScanner_ReadFeature(normalized);
                if (feature <= 0) continue;

                hits.Add(new ScanHit
                {
                    Path = normalized,
                    Feature = feature,
                    Version = ReadVersion(normalized) ?? Path.GetFileName(normalized),
                    Source = label,
                    AlreadyManaged = managed.Contains(normalized),
                });
            }
        }
        return hits.OrderByDescending(h => h.Feature).ThenBy(h => h.Path).ToList();
    }

    private static string Normalize(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd('\\', '/'); }
        catch { return p; }
    }

    private static IEnumerable<(string root, string label, int depth)> Roots()
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roam = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        yield return (Path.Combine(user, "scoop", "apps"), "scoop", 1);
        yield return (Path.Combine(la, "scoop", "apps"), "scoop", 1);
        yield return (Path.Combine(pf, "Eclipse Adoptium"), "系统", 1);
        yield return (Path.Combine(pf, "Java"), "系统", 1);
        yield return (Path.Combine(pf, "BellSoft"), "系统", 1);
        yield return (Path.Combine(pf, "Amazon Corretto"), "系统", 1);
        yield return (Path.Combine(pf, "Zulu"), "系统", 1);
        yield return (Path.Combine(pf, "Microsoft"), "系统", 2);
        yield return (Path.Combine(pf86, "Java"), "系统", 1);
        yield return (Path.Combine(user, ".jdks"), "IDEA", 1);
        yield return (Path.Combine(user, ".gradle", "jdks"), "Gradle", 1);
        yield return (Path.Combine(user, "Java"), "JavaDeck", 1);
        yield return (Path.Combine(user, "AppData", "Roaming", ".minecraft", "runtime"), "Minecraft", 3);
        yield return (@"C:\Java", "系统", 1);
        yield return (@"D:\Java", "系统", 1);
    }

    private static IEnumerable<string> Expand(string root, int depth, CancellationToken ct)
    {
        var result = new List<string>();
        Collect(root, depth, result, ct);
        return result;
    }

    private static void Collect(string dir, int depth, List<string> acc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            acc.Add(dir);
            if (depth <= 0) return;
            foreach (var sub in Directory.EnumerateDirectories(dir))
                Collect(sub, depth - 1, acc, ct);
        }
        catch { }
    }

    private static string? ResolveJdkHome(string dir)
    {
        try
        {
            if (File.Exists(Path.Combine(dir, "bin", "java.exe"))) return dir;

            var current = Path.Combine(dir, "current");
            if (File.Exists(Path.Combine(current, "bin", "java.exe"))) return current;

            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (File.Exists(Path.Combine(sub, "bin", "java.exe"))) return sub;
                foreach (var sub2 in Directory.EnumerateDirectories(sub))
                    if (File.Exists(Path.Combine(sub2, "bin", "java.exe"))) return sub2;
            }
        }
        catch { }
        return null;
    }

    private static int JdkScanner_ReadFeature(string home)
    {
        var v = ReadVersion(home);
        if (string.IsNullOrEmpty(v)) return 0;
        if (v.StartsWith("1."))
            return int.TryParse(v.Split('.')[1], out var n) ? n : 0;
        var major = new string(v.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(major, out var m) ? m : 0;
    }

    public static int ReadFeature(string home) => JdkScanner_ReadFeature(home);

    public static string? ReadVersion(string home)
    {
        try
        {
            var release = Path.Combine(home, "release");
            if (File.Exists(release))
            {
                foreach (var line in File.ReadLines(release))
                {
                    if (!line.TrimStart().StartsWith("JAVA_VERSION=", StringComparison.OrdinalIgnoreCase)) continue;
                    return line.Split('=', 2)[1].Trim().Trim('"');
                }
            }
        }
        catch { }

        var name = Path.GetFileName(home.TrimEnd('\\', '/'));
        var idx = name.IndexOf("jdk", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var tail = name[(idx + 3)..].TrimStart('-', '_', ' ');
            var digits = new string(tail.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
            if (digits.Length > 0) return digits.Trim('.');
        }
        return null;
    }
}
