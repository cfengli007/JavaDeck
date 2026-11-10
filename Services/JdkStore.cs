using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Text.Json;

namespace JavaDeck.Services;

public sealed class InstalledJdk
{
    public string Id { get; set; } = "";
    public int FeatureVersion { get; set; }
    public string Version { get; set; } = "";
    public string Path { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime InstalledAt { get; set; }
    public string Sha1 { get; set; } = "";

    /// <summary>standard / full / lite —— 从安装来源记录，也用于界面区分</summary>
    public string Variant { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string VariantLabel => (Variant ?? "") switch
    {
        "full" => "Full",
        "lite" => "Lite",
        "standard" => "标准",
        _ => "未知",
    };

    /// <summary>有变体信息才显示徽标</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasVariant => !string.IsNullOrEmpty(Variant);
}

public sealed class JdkStore
{
    private readonly string _root;
    private readonly string _metaFile;

    public JdkStore(string root)
    {
        _root = root;
        _metaFile = Path.Combine(root, "jdk-index.json");
    }

    public string Root => _root;

    public string LinkPath(int feature) => Path.Combine(_root, "current-" + feature);

    public string CommonLinkPath() => Path.Combine(_root, "current");

    public List<InstalledJdk> LoadAll()
    {
        try
        {
            if (!File.Exists(_metaFile))
            {
                var scanned = ScanDirectory();
                if (scanned.Count > 0) SaveAll(scanned);   // 索引丢失时自动重建
                return scanned;
            }

            var list = JsonSerializer.Deserialize<List<InstalledJdk>>(File.ReadAllText(_metaFile)) ?? new();
            var alive = list.Where(j => Directory.Exists(j.Path)).OrderByDescending(j => j.FeatureVersion).ToList();

            // 变体以磁盘为准：目录被替换后索引里的旧值会过时
            var changed = false;
            foreach (var j in alive)
            {
                var actual = DetectVariant(j.Path);
                if (!string.IsNullOrEmpty(actual) && !string.Equals(actual, j.Variant, StringComparison.OrdinalIgnoreCase))
                {
                    j.Variant = actual;
                    changed = true;
                }
            }
            if (changed) SaveAll(alive);

            // 索引里一条都没有但目录里有 JDK：说明索引损坏/被删，自动重建
            if (alive.Count == 0)
            {
                var scanned = ScanDirectory();
                if (scanned.Count > 0) { SaveAll(scanned); return scanned; }
            }
            return alive;
        }
        catch { return ScanDirectory(); }
    }

    private List<InstalledJdk> ScanDirectory()
    {
        var result = new List<InstalledJdk>();
        if (!Directory.Exists(_root)) return result;
        foreach (var dir in Directory.GetDirectories(_root))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith("current", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith(".staging", StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(Path.Combine(dir, "bin", "java.exe"))) continue;
            var feature = DetectFeature(Path.Combine(dir, "release"));
            result.Add(new InstalledJdk
            {
                Id = name,
                FeatureVersion = feature,
                Version = name,
                Path = dir,
                InstalledAt = Directory.GetCreationTime(dir),
                Variant = DetectVariant(dir),
            });
        }
        return result.OrderByDescending(j => j.FeatureVersion).ToList();
    }

    /// <summary>
    /// 推断已装目录的变体。三级策略：
    ///   1. 用发布清单缓存里同版本的文件名反查（最准：文件名带 -full/-lite 后缀）
    ///   2. 有 JavaFX → full（BellSoft 的 full 就是 JDK+JavaFX）
    ///   3. 无 JavaFX：按体积对照标准版基准，明显偏小 → lite
    /// </summary>
    public static string DetectVariant(string jdkPath)
    {
        try
        {
            var relFile = Path.Combine(jdkPath, "release");
            var feature = DetectFeature(relFile);
            var size = DirSize(jdkPath);

            // ---- JavaFX 是 full 的硬标志 ----
            var binDir = Path.Combine(jdkPath, "bin");
            var libDir = Path.Combine(jdkPath, "lib");
            var hasJavafx = false;

            if (File.Exists(relFile))
            {
                try
                {
                    foreach (var line in File.ReadLines(relFile))
                    {
                        if (!line.TrimStart().StartsWith("MODULES=", StringComparison.OrdinalIgnoreCase)) continue;
                        hasJavafx = line.Contains("javafx.", StringComparison.OrdinalIgnoreCase);
                        break;
                    }
                }
                catch { }
            }
            if (!hasJavafx)
            {
                try
                {
                    hasJavafx = (Directory.Exists(libDir) && Directory.GetFiles(libDir, "javafx*.jar").Length > 0)
                             || (Directory.Exists(binDir) && Directory.GetFiles(binDir, "javafx*.dll").Length > 0);
                }
                catch { }
            }
            if (hasJavafx) return "full";

            // ---- 3) 按体积判定 lite ----
            var sizeMb = size / 1048576;
            if (sizeMb <= 0) return "";
            if (StandardSizeMb.TryGetValue(feature, out var baseline) && baseline > 0)
                return sizeMb < baseline * 0.75 ? "lite" : "standard";
            return sizeMb < 150 ? "lite" : "standard";
        }
        catch { }
        return "";
    }

    private static readonly Dictionary<int, int> StandardSizeMb = new()
    {
        [8] = 190, [11] = 326, [17] = 337, [21] = 364, [25] = 449,
    };

    private static string? ReadJavaVersion(string releaseFile)
    {
        try
        {
            if (!File.Exists(releaseFile)) return null;
            foreach (var line in File.ReadLines(releaseFile))
            {
                if (!line.TrimStart().StartsWith("JAVA_VERSION=", StringComparison.OrdinalIgnoreCase)) continue;
                return line.Split('=', 2)[1].Trim().Trim('"');
            }
        }
        catch { }
        return null;
    }

    private static int DetectFeature(string releaseFile)
    {
        try
        {
            if (!File.Exists(releaseFile)) return 0;
            foreach (var line in File.ReadAllLines(releaseFile))
            {
                if (!line.TrimStart().StartsWith("JAVA_VERSION=", StringComparison.OrdinalIgnoreCase)) continue;
                var v = line.Split('=', 2)[1].Trim().Trim('"');
                if (v.StartsWith("1.")) return int.TryParse(v.Split('.')[1], out var n) ? n : 0;
                var major = new string(v.TakeWhile(char.IsDigit).ToArray());
                return int.TryParse(major, out var m) ? m : 0;
            }
        }
        catch { }
        return 0;
    }

    public void SaveAll(List<InstalledJdk> list)
    {
        try
        {
            Directory.CreateDirectory(_root);
            // 保证 Id 唯一，避免同批次导入全部同名
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var j in list)
            {
                var baseId = string.IsNullOrWhiteSpace(j.Id) ? "jdk" : j.Id;
                var id = baseId;
                var n = 2;
                while (!seen.Add(id)) id = baseId + "-" + n++;
                j.Id = id;
            }
            File.WriteAllText(_metaFile, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>把压缩包内唯一顶层目录解压到 root\&lt;id&gt;。同步方法，调用方负责丢到后台线程。</summary>
    public InstalledJdk Extract(string zipFile, LibericaRelease rel, IProgress<string>? log, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_root);
        var tempDir = Path.Combine(_root, ".staging-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            log?.Report("解压中…");
            ZipFile.ExtractToDirectory(zipFile, tempDir);

            var tops = Directory.GetDirectories(tempDir);
            var src = tops.Length == 1 ? tops[0] : tempDir;
            var id = "jdk-" + rel.Version.Replace('+', '.');
            var dest = Path.Combine(_root, id);

            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Directory.Move(src, dest);

            log?.Report("已解压到 " + dest);
            return new InstalledJdk
            {
                Id = id,
                FeatureVersion = rel.FeatureVersion,
                Version = rel.Version,
                Path = dest,
                SizeBytes = DirSize(dest),
                InstalledAt = DateTime.Now,
                Sha1 = rel.Sha1,
                Variant = rel.Variant,
            };
        }
        finally
        {
            // 无论成功失败，暂存目录必须清掉，否则会累积几百 MB 垃圾
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }

        return null;   // 不可达
    }

    /// <summary>清理残留的 .staging-* 目录（上次崩溃/中断留下的）。</summary>
    public int CleanStaging()
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(_root)) return 0;
            foreach (var dir in Directory.GetDirectories(_root, ".staging-*"))
            {
                try { Directory.Delete(dir, true); removed++; } catch { }
            }
        }
        catch { }
        return removed;
    }

    public static long DirSize(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch { return 0; }
    }

    public void Delete(InstalledJdk jdk, IProgress<string>? log)
    {
        foreach (var link in Directory.GetFiles(_root).Concat(Directory.GetDirectories(_root)))
        {
            var fsi = new DirectoryInfo(link);
            if (!fsi.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            var target = fsi.LinkTarget;
            if (target == null) continue;
            var full = Path.GetFullPath(Path.Combine(_root, target));
            if (string.Equals(full.TrimEnd('\\'), jdk.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                log?.Report("移除链接 " + Path.GetFileName(link));
                Directory.Delete(link, false);
            }
        }
        log?.Report("删除目录 " + jdk.Path);
        if (Directory.Exists(jdk.Path)) Directory.Delete(jdk.Path, true);
    }
}