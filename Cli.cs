using System.IO;
using JavaDeck.Services;

namespace JavaDeck;

/// <summary>无界面命令行（JavaDeck.exe &lt;verb&gt; ...），便于脚本化与自检。</summary>
public static class Cli
{
    private static readonly string[] Verbs = { "list", "scan", "import", "use", "env", "install", "remove", "preview" };

    /// <summary>是否是 CLI 动作（决定要不要显示窗口）。</summary>
    public static bool IsVerb(string arg) => Verbs.Contains(arg.ToLowerInvariant());

    public static int Run(string[] args)
    {
        try { return RunCore(args); }
        catch (Exception ex)
        {
            Console.WriteLine("错误: " + ex.Message);
            return 9;
        }
    }

    private static int RunCore(string[] args)
    {
        var verb = args[0].ToLowerInvariant();
        switch (verb)
        {
            case "list": return List();
            case "scan": return Scan();
            case "import": return Import();
            case "use": return Use(args);
            case "env": return Env();
            case "install": return Install(args).GetAwaiter().GetResult();
            case "remove": return Remove(args);
            case "preview": return Preview();
            default:
                Console.WriteLine("用法: JavaDeck <list|scan|import|use|env|install|remove> [参数]");
                return 1;
        }
    }

    private static JdkStore Store()
    {
        var s = AppSettings.Load();
        Console.WriteLine("# root=" + s.RootDir);
        return new JdkStore(s.RootDir);
    }

    private static int List()
    {
        var store = Store();
        var list = store.LoadAll();
        if (list.Count == 0) { Console.WriteLine("(未登记任何 JDK)"); return 0; }
        var home = EnvManager.GetJdkHome(out _);
        foreach (var j in list)
        {
            var active = home != null &&
                (string.Equals(home, j.Path, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(home, store.LinkPath(j.FeatureVersion), StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"{(active ? "*" : " ")} Java {j.FeatureVersion,2}  {j.Version,-22} {j.Path}");
        }
        return 0;
    }

    private static int Scan()
    {
        var store = Store();
        var hits = JdkScanner.Scan(store.LoadAll().Select(i => i.Path));
        if (hits.Count == 0) { Console.WriteLine("(未发现)"); return 0; }
        foreach (var h in hits)
            Console.WriteLine($"  [{h.Source,-8}] Java {h.Feature,2}  {h.Version,-24} {(h.AlreadyManaged ? "(已登记)" : "")} {h.Path}");
        return 0;
    }

    private static int Import()
    {
        var store = Store();
        var existing = store.LoadAll();
        var hits = JdkScanner.Scan(existing.Select(i => i.Path));
        var added = 0;
        foreach (var h in hits.Where(h => !h.AlreadyManaged))
        {
            existing.Add(new InstalledJdk
            {
                Id = "import-" + h.Feature + "-" + System.IO.Path.GetFileName(h.Path.TrimEnd('\\')),
                FeatureVersion = h.Feature,
                Version = h.Version,
                Path = h.Path,
                SizeBytes = JdkStore.DirSize(h.Path),
                InstalledAt = DateTime.Now,
            });
            added++;
            Console.WriteLine("  + Java " + h.Feature + " " + h.Version);
        }
        store.SaveAll(existing);
        Console.WriteLine($"已导入 {added} 个");
        return 0;
    }

    private static int Use(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("用法: use <版本号|Java主版本> [--no-link]"); return 1; }
        var store = Store();
        var key = args[1];
        var list = store.LoadAll();
        var target = list.FirstOrDefault(j => j.Version == key)
                  ?? list.FirstOrDefault(j => j.Version.Split('+')[0] == key)
                  ?? (int.TryParse(key, out var fv) ? list.Where(j => j.FeatureVersion == fv).OrderByDescending(j => j.InstalledAt).FirstOrDefault() : null);
        if (target == null) { Console.WriteLine("找不到匹配的 JDK: " + key); return 2; }

        var home = target.Path;
        if (!args.Contains("--no-link"))
        {
            var link = store.LinkPath(target.FeatureVersion);
            try
            {
                Junction.Create(link, target.Path);
                home = link;
                Console.WriteLine("junction: " + link + " -> " + target.Path);
            }
            catch (Exception ex)
            {
                Console.WriteLine("建链接失败（回退为直接指向真实目录）: " + ex.Message);
            }
        }
        EnvManager.SwitchJavaHome(home);
        Console.WriteLine("JAVA_HOME(user) = " + home);
        Console.WriteLine("java -version: " + (EnvManager.ProbeJavaVersion(home) ?? "(failed)"));
        return 0;
    }

    private static int Preview()
    {
        var raw = EnvManager.GetRawPath() ?? "";
        Console.WriteLine("--- 当前用户 PATH ---");
        foreach (var p in EnvManager.SplitPath(raw)) Console.WriteLine("  " + p);
        Console.WriteLine();
        Console.WriteLine("--- JDK 条目 ---");
        foreach (var p in EnvManager.SplitPath(raw).Where(EnvManager.LooksLikeJdkBin))
            Console.WriteLine("  " + p);
        return 0;
    }

    private static int Env()
    {
        var home = EnvManager.GetJdkHome(out var isSys);
        Console.WriteLine("JAVA_HOME = " + (home ?? "(none)") + (isSys ? "  [MACHINE]" : "  [USER]"));
        var up = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        foreach (var p in up.Split(';', StringSplitOptions.RemoveEmptyEntries).Where(p => p.Contains("java", StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine("PATH: " + p);
        if (home != null) Console.WriteLine("java -version: " + (EnvManager.ProbeJavaVersion(home) ?? "(failed)"));
        return 0;
    }

    private static async Task<int> Install(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("用法: install <主版本号> [--latest|--all]"); return 1; }
        if (!int.TryParse(args[1], out var fv)) { Console.WriteLine("主版本号无效"); return 1; }
        var store = Store();
        var api = new LibericaClient();
        var list = await api.GetAllAsync(fv);
        var latest = api.PickLatest(list, "standard");
        if (latest == null) { Console.WriteLine("没有可用版本"); return 2; }

        var dir = Path.Combine(Path.GetTempPath(), "JavaDeck");
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, latest.FileName);
        if (File.Exists(zip) && new FileInfo(zip).Length != latest.Size) File.Delete(zip);
        if (!File.Exists(zip))
        {
            Console.WriteLine($"下载 {latest.FileName} ({latest.Size / 1048576} MB)…");
            var last = 0;
            var progress = new Progress<(long done, long total)>(p =>
            {
                var pct = (int)(p.done * 100 / Math.Max(1, p.total));
                if (pct >= last + 10) { last = pct - pct % 10; Console.WriteLine($"  {last}%  ({p.done / 1048576} MB)"); }
            });
            await api.DownloadParallelAsync(latest.DownloadUrl, zip, latest.Size, latest.Sha1, progress, default);
            Console.WriteLine("  sha1 校验通过");
        }
        var jdk = store.Extract(zip, latest, new Progress<string>(s => Console.WriteLine(s)));
        var all = store.LoadAll();
        all.RemoveAll(j => string.Equals(j.Path, jdk.Path, StringComparison.OrdinalIgnoreCase));
        all.Add(jdk);
        store.SaveAll(all);
        try { File.Delete(zip); } catch { }
        Console.WriteLine("安装完成: " + jdk.Path);
        return 0;
    }

    private static int Remove(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("用法: remove <版本号>"); return 1; }
        var store = Store();
        var all = store.LoadAll();
        var t = all.FirstOrDefault(j => j.Version == args[1]) ?? (int.TryParse(args[1], out var fv) ? all.FirstOrDefault(j => j.FeatureVersion == fv) : null);
        if (t == null) { Console.WriteLine("未找到"); return 2; }
        store.Delete(t, new Progress<string>(s => Console.WriteLine(s)));
        all.RemoveAll(j => j.Path == t.Path);
        store.SaveAll(all);
        return 0;
    }

    private static readonly List<string> _noop = new();
}