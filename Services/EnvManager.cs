using System.IO;
using System.Runtime.InteropServices;

namespace JavaDeck.Services;

/// <summary>
/// 环境变量规则（用户明确要求）：
///   • 安装新 JDK  → 把它的 bin 追加到 PATH **末尾**
///   • 设为 JAVA_HOME → 把它从 PATH 里**移除**（JAVA_HOME 不参与 PATH 排序）
///   • JAVA_HOME 换成别的版本 → 原 JAVA_HOME 那份的 bin 重新**加回 PATH**
/// </summary>
public static class EnvManager
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    public const string JavaHomeBin = @"%JAVA_HOME%\bin";

    /// <summary>
    /// 广播环境变量变更。
    /// 优化：用 SMTO_NOTIMEOUTIFNOTHUNG + 1000ms，且异步投递，
    /// 避免顶层窗口繁忙时阻塞调用方 3 秒。
    /// </summary>
    public static void Broadcast()
    {
        try
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    SendMessageTimeout(new IntPtr(0xffff), WM_SETTINGCHANGE, IntPtr.Zero,
                        "Environment", SMTO_ABORTIFHUNG, 1000, out IntPtr _);
                }
                catch { }
            });
        }
        catch { }
    }

    public static string? GetJdkHome(out bool isSystem)
    {
        isSystem = false;
        var user = Environment.GetEnvironmentVariable("JAVA_HOME", EnvironmentVariableTarget.User);
        if (!string.IsNullOrWhiteSpace(user)) return user;
        var machine = Environment.GetEnvironmentVariable("JAVA_HOME", EnvironmentVariableTarget.Machine);
        if (!string.IsNullOrWhiteSpace(machine)) { isSystem = true; return machine; }
        return null;
    }

    public static string? GetRawPath() =>
        Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);

    public static List<string> SplitPath(string? raw) =>
        (raw ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
                   .Select(p => p.Trim())
                   .Where(p => p.Length > 0)
                   .ToList();

    private static void WritePath(IEnumerable<string> entries)
    {
        var joined = string.Join(";", entries);
        if (joined.Length > 30000) joined = joined[..30000];
        Environment.SetEnvironmentVariable("PATH", joined, EnvironmentVariableTarget.User);
    }

    /// <summary>把某 JDK 的 bin 追加到 PATH 末尾（已存在则不重复添加）。</summary>
    public static bool AddJdkToPath(string jdkHome)
    {
        var bin = Path.Combine(jdkHome, "bin");
        var list = SplitPath(GetRawPath());
        if (list.Any(p => SamePath(p, bin))) return false;
        list.Add(bin);
        WritePath(list);
        Broadcast();
        return true;
    }

    /// <summary>从 PATH 中移除某个目录。</summary>
    public static bool RemoveFromPath(string jdkHome)
    {
        var bin = Path.Combine(jdkHome, "bin");
        var list = SplitPath(GetRawPath());
        var removed = list.RemoveAll(p => SamePath(p, bin)) > 0;
        if (removed)
        {
            WritePath(list);
            Broadcast();
        }
        return removed;
    }

    /// <summary>
    /// 切换 JAVA_HOME：
    ///   1. 旧 JAVA_HOME 对应的 bin 加回 PATH 末尾
    ///   2. 新 JAVA_HOME 对应的 bin 从 PATH 移除
    ///   3. 写入新的 JAVA_HOME
    /// </summary>
    public static void SwitchJavaHome(string? newHome)
    {
        var oldHome = Environment.GetEnvironmentVariable("JAVA_HOME", EnvironmentVariableTarget.User);
        var list = SplitPath(GetRawPath());

        // 1) 旧 home 的 bin 回填到 PATH（如果它还没在里面）
        if (!string.IsNullOrWhiteSpace(oldHome) &&
            !string.Equals(oldHome, newHome, StringComparison.OrdinalIgnoreCase))
        {
            var oldBin = Path.Combine(oldHome, "bin");
            if (!list.Any(p => SamePath(p, oldBin)))
                list.Add(oldBin);
        }

        // 2) 新 home 的 bin 从 PATH 里摘掉
        if (!string.IsNullOrWhiteSpace(newHome))
        {
            var newBin = Path.Combine(newHome, "bin");
            list.RemoveAll(p => SamePath(p, newBin));
        }

        WritePath(list);
        Environment.SetEnvironmentVariable("JAVA_HOME", newHome ?? "", EnvironmentVariableTarget.User);
        Broadcast();
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            var pa = Environment.ExpandEnvironmentVariables(a).TrimEnd('\\', '/');
            var pb = Environment.ExpandEnvironmentVariables(b).TrimEnd('\\', '/');
            return string.Equals(pa, pb, StringComparison.OrdinalIgnoreCase);
        }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    /// <summary>判断某个 PATH 项是否指向某套 JDK 的 bin 目录。</summary>
    public static bool LooksLikeJdkBin(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return false;
        var trimmed = entry.Trim().TrimEnd('\\', '/');
        if (!trimmed.EndsWith("bin", StringComparison.OrdinalIgnoreCase)) return false;
        if (trimmed.Contains('%')) return true;

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(trimmed);
            if (File.Exists(Path.Combine(expanded, "java.exe"))) return true;
            var dir = new DirectoryInfo(expanded).Parent;
            for (var up = 0; up < 3 && dir != null; up++, dir = dir.Parent)
            {
                var leaf = dir.Name;
                if (leaf.Contains("jdk", StringComparison.OrdinalIgnoreCase)
                    || leaf.Contains("jre", StringComparison.OrdinalIgnoreCase)
                    || leaf.Contains("java", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch { return false; }
    }

    public static string? ProbeJavaVersion(string jdkHome)
    {
        try
        {
            var exe = Path.Combine(jdkHome, "bin", "java.exe");
            if (!File.Exists(exe)) return null;
            var psi = new System.Diagnostics.ProcessStartInfo(exe, "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;
            var task = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(5000)) { try { proc.Kill(true); } catch { } return "(超时)"; }
            var err = task.GetAwaiter().GetResult();
            var line = err.Split('\n').FirstOrDefault(l => l.Contains("version"))?.Trim();
            return string.IsNullOrWhiteSpace(line) ? null : line;
        }
        catch { return null; }
    }
}