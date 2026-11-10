using System.IO;
using System.Text;
using JavaDeck.Services;

namespace JavaDeck;

/// <summary>命令行自检：JavaDeck.exe --selftest</summary>
public static class SelfTest
{
    public static int Run(string[] args)
    {
        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); Console.WriteLine(s); }

        try
        {
            W("== JavaDeck selftest ==");
            W("OS: " + Environment.OSVersion);
            W(".NET: " + Environment.Version);

            var settings = AppSettings.Load();
            W("RootDir: " + settings.RootDir);

            var store = new JdkStore(settings.RootDir);
            var installed = store.LoadAll();
            W($"Managed JDKs: {installed.Count}");
            foreach (var j in installed) W($"  - Java {j.FeatureVersion} {j.Version} :: {j.Path}");

            W("");
            W("== scan ==");
            var hits = JdkScanner.Scan(installed.Select(i => i.Path), new Progress<string>(_ => { }));
            W($"Found: {hits.Count}");
            foreach (var h in hits) W($"  [{h.Source}] Java {h.Feature}  {h.Version}  {h.Path}");

            W("");
            W("== env ==");
            var home = EnvManager.GetJdkHome(out var isSys);
            W("JAVA_HOME: " + (home ?? "(none)") + (isSys ? "  [MACHINE]" : "  [USER]"));
            if (home != null) W("java -version: " + (EnvManager.ProbeJavaVersion(home) ?? "(failed)"));
            var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
            W("user PATH java entries: " + string.Join(" | ",
                userPath.Split(';', StringSplitOptions.RemoveEmptyEntries).Where(p => p.Contains("java", StringComparison.OrdinalIgnoreCase))));

            W("");
            W("== launchers ==");
            foreach (var l in LauncherIntegration.Detect()) W($"  {l.Name}  {l.ExePath ?? l.GameDir}");
            var gv = LauncherIntegration.InstalledGameVersions();
            W("mc versions installed: " + gv.Count + (gv.Count > 0 ? " (" + string.Join(", ", gv.Take(8)) + ")" : ""));

            W("");
            W("== network ==");
            try
            {
                var api = new LibericaClient();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var list = api.GetAllAsync(21).GetAwaiter().GetResult();
                var latest = api.PickLatest(list, "standard");
                W($"BellSoft API OK ({sw.ElapsedMilliseconds} ms), {list.Count} builds, latest={latest?.Version}");
            }
            catch (Exception ex) { W("BellSoft API FAIL: " + ex.Message); }

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var mc = new MojangApi().GetReleasesAsync(null, CancellationToken.None).GetAwaiter().GetResult();
                W($"Mojang API OK ({sw.ElapsedMilliseconds} ms), {mc.Count} releases");
            }
            catch (Exception ex) { W("Mojang API FAIL: " + ex.Message); }
        }
        catch (Exception ex)
        {
            W("SELFTEST CRASH: " + ex);
            return 2;
        }
        return 0;
    }
}