using System.IO;
using System.Text.Json;

namespace JavaDeck.Services;

public sealed class UpdateInfo
{
    public int Feature { get; set; }
    public string InstalledVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public string Variant { get; set; } = "standard";
    public LibericaRelease? Release { get; set; }
    public bool HasUpdate => !string.IsNullOrEmpty(LatestVersion) && LatestVersion != InstalledVersion;
}

/// <summary>
/// 更新检查。
/// 策略（按官方 API 文档）：
///   • 对每个已装 JDK 发一次「按 version 查询」，读 latestInFeatureVersion 标志
///   • 该标志为 true → 已是最新，无需任何比较或缓存
///   • 为 false → 再用 version-modifier=latest 拿该大版本最新构建（含下载链接）
/// 开销：每个 JDK 约 640 字节，有更新时额外 1.2 KB。
/// 不涉及任何列表缓存。
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    private readonly LibericaClient _api;
    private readonly JdkStore _store;
    private Timer? _timer;

    public UpdateChecker(LibericaClient api, JdkStore store)
    {
        _api = api;
        _store = store;
    }

    public void Start(TimeSpan interval, Action<List<UpdateInfo>> onResult, Action<string>? onError = null)
    {
        Stop();
        _timer = new Timer(async _ =>
        {
            try { onResult(await CheckAsync()); }
            catch (Exception ex) { onError?.Invoke(ex.Message); }
        }, null, TimeSpan.FromSeconds(3), interval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public async Task<List<UpdateInfo>> CheckAsync(CancellationToken ct = default)
    {
        var installed = _store.LoadAll();
        var results = new List<UpdateInfo>();

        foreach (var jdk in installed.GroupBy(j => j.FeatureVersion))
        {
            ct.ThrowIfCancellationRequested();
            var current = jdk.OrderByDescending(j => j.InstalledAt).First();

            var bundleType = current.Variant switch
            {
                "full" => "jdk-full",
                "lite" => "jdk-lite",
                _ => "jdk",
            };

            VersionStatus? status;
            try { status = await _api.CheckVersionAsync(current.FeatureVersion, current.Version, bundleType, ct); }
            catch (OperationCanceledException) { throw; }
            catch { continue; }

            if (status == null) continue;

            var info = new UpdateInfo
            {
                Feature = current.FeatureVersion,
                InstalledVersion = current.Version,
                Variant = current.Variant,
            };

            // 官方标志：true 就是最新，直接跳过
            if (status.IsLatestInFeature)
            {
                info.LatestVersion = current.Version;
                continue;
            }

            // 不是最新 → 查该大版本最新构建
            try
            {
                var latest = await _api.GetLatestAsync(current.FeatureVersion, bundleType, ct);
                if (latest != null && LibericaClient.BuildKey(latest.Version) > LibericaClient.BuildKey(current.Version))
                {
                    info.LatestVersion = latest.Version;
                    info.Release = latest;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            if (info.HasUpdate) results.Add(info);
        }

        return results;
    }

    public void Dispose() => Stop();
}

public sealed class UpdateSettings
{
    public bool Enabled { get; set; } = true;
    public int IntervalHours { get; set; } = 24;
    public DateTime? LastCheck { get; set; }

    private static string PathFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JavaDeck", "update.json");

    public static UpdateSettings Load()
    {
        try
        {
            if (File.Exists(PathFile))
                return JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(PathFile)) ?? new();
        }
        catch { }
        return new();
    }

    public bool Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(PathFile)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(PathFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex)
        {
            SaveError = ex.Message;
            return false;
        }
    }

    public static string? SaveError { get; private set; }
}
