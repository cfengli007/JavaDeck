using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JavaDeck.Services;

public sealed class LibericaRelease
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("featureVersion")] public int FeatureVersion { get; set; }
    [JsonPropertyName("filename")] public string FileName { get; set; } = "";
    [JsonPropertyName("downloadUrl")] public string DownloadUrl { get; set; } = "";
    [JsonPropertyName("sha1")] public string Sha1 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("LTS")] public bool Lts { get; set; }
    [JsonPropertyName("EOL")] public bool Eol { get; set; }
    [JsonPropertyName("latestInFeatureVersion")] public bool LatestInFeatureVersion { get; set; }
    [JsonPropertyName("buildVersion")] public int BuildVersion { get; set; }
    [JsonPropertyName("GA")] public bool Ga { get; set; }
    [JsonPropertyName("bundleType")] public string BundleType { get; set; } = "";
    [JsonPropertyName("packageType")] public string PackageType { get; set; } = "";
    [JsonPropertyName("FX")] public bool Fx { get; set; }
    [JsonPropertyName("latest")] public bool Latest { get; set; }
    [JsonPropertyName("latestLTS")] public bool LatestLts { get; set; }

    /// <summary>
    /// 变体判定——**直接用文件名后缀**，这是唯一可靠的来源。
    /// API 返回的 filename 明确带后缀：
    ///   bellsoft-jdk21.0.7+9-windows-amd64.zip        → standard
    ///   bellsoft-jdk21.0.7+9-windows-amd64-full.zip   → full
    ///   bellsoft-jdk21.0.7+9-windows-amd64-lite.zip   → lite
    /// bundleType 字段同步为 jdk / jdk-full / jdk-lite，可互为校验。
    /// </summary>
    public string Variant => VariantFromFileName(FileName);

    public static string VariantFromFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return "standard";
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (name.EndsWith("-full", StringComparison.OrdinalIgnoreCase)) return "full";
        if (name.EndsWith("-lite", StringComparison.OrdinalIgnoreCase)) return "lite";
        return "standard";
    }

    public string VariantLabel => Variant switch
    {
        "full" => "Full",
        "lite" => "Lite",
        _ => "标准",
    };

    public string DisplayName => $"Java {FeatureVersion} · {Version} · {VariantLabel}";
}

public sealed class VersionStatus
{
    public string Version { get; set; } = "";
    public int FeatureVersion { get; set; }
    public bool IsLatestInFeature { get; set; }
    public bool IsLatestOverall { get; set; }
    public bool IsLts { get; set; }
    public string Variant { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Sha1 { get; set; } = "";
    public long Size { get; set; }
}

public sealed class LibericaClient
{
    private static HttpClient SharedHttp = CreateClient();
    private readonly HttpClient _http;

    /// <summary>代理设置变更后重建底层 HttpClient。</summary>
    public static void RefreshHandler(IWebProxy? proxy)
    {
        try
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(8),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 8,
            };
            if (proxy != null) handler.Proxy = proxy;
            SharedHttp.Dispose();
            SharedHttp = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
            SharedHttp.DefaultRequestHeaders.UserAgent.ParseAdd("JavaDeck/2.1");
        }
        catch { }
    }

    public LibericaClient() : this(SharedHttp) { }

    /// <summary>用于测试注入自定义 handler。</summary>
    public LibericaClient(HttpMessageHandler handler)
        : this(new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) }) { }

    private LibericaClient(HttpClient http) => _http = http;

    public static readonly (string bundle, string variant)[] Bundles =
    {
        ("jdk", "standard"),
        ("jdk-full", "full"),
        ("jdk-lite", "lite"),
    };

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 8,
        };
        var h = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("JavaDeck/1.8");
        return h;
    }

    /// <summary>版本号 → 单调整数，每段 4 位十进制 × 5 段。</summary>
    public static long BuildKey(string version)
    {
        var head = version.Split('+')[0];
        var segs = head.Split('.', '-', '_');
        long key = 0;
        var used = 0;
        foreach (var seg in segs)
        {
            if (used >= 5) break;
            var digits = new string(seg.TakeWhile(char.IsDigit).ToArray());
            var n = digits.Length > 0 && long.TryParse(digits, out var v) ? v : 0;
            if (n > 9999) n = 9999;
            key = key * 10000 + n;
            used++;
        }
        while (used++ < 5) key *= 10000;
        return key;
    }

    public LibericaRelease? PickLatest(List<LibericaRelease> list, string variant = "standard")
    {
        if (list.Count == 0) return null;
        var same = list.Where(r => r.Variant == variant).ToList();
        if (same.Count == 0) return null;
        var flagged = same.Where(r => r.LatestInFeatureVersion).ToList();
        var pool = flagged.Count > 0 ? flagged : same;
        return pool.OrderByDescending(r => BuildKey(r.Version))
                   .ThenByDescending(r => r.BuildVersion)
                   .FirstOrDefault();
    }

    public sealed class FetchResult
    {
        /// <summary>true = 服务端返回 304，内容未变</summary>
        public bool NotModified { get; init; }
        public List<LibericaRelease> Releases { get; init; } = new();
        public string? LastModified { get; init; }
        public string? ETag { get; init; }
    }

    /// <summary>
    /// 拉取某个变体的清单，支持条件请求。
    /// 传入 lastModified / etag 时，若服务端返回 304 则完全不下发响应体。
    /// 实测 BellSoft API 15 个端点全部支持 304。
    /// </summary>
    public async Task<FetchResult> GetVariantAsync(int feature, string bundleType,
        string? lastModified = null, string? etag = null, CancellationToken ct = default)
    {
        var url = "https://api.bell-sw.com/v1/liberica/releases" +
                  $"?version-feature={feature}&os=windows&arch=x86&bitness=64" +
                  $"&bundle-type={bundleType}&package-type=zip";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(etag)) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        if (!string.IsNullOrEmpty(lastModified)) req.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);

        using var resp = await _http.SendAsync(req, ct);

        var lm = resp.Content.Headers.LastModified?.ToString("R");
        var et = resp.Headers.ETag?.Tag;

        if (resp.StatusCode == System.Net.HttpStatusCode.NotModified)
            return new FetchResult { NotModified = true, LastModified = lm, ETag = et };

        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        var list = await JsonSerializer.DeserializeAsync<List<LibericaRelease>>(s, cancellationToken: ct) ?? new();

        return new FetchResult
        {
            NotModified = false,
            Releases = list.Where(r => r.Ga).ToList(),
            LastModified = lm,
            ETag = et,
        };
    }

    public async Task<List<LibericaRelease>> GetAllAsync(int feature, CancellationToken ct = default)
    {
        var result = new List<LibericaRelease>();
        foreach (var (bundle, _) in Bundles)
        {
            try
            {
                var r = await GetVariantAsync(feature, bundle, ct: ct);
                result.AddRange(r.Releases);
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        return result;
    }

    /// <summary>
    /// 查询某个已装版本是否是该大版本的最新构建（官方 latestInFeatureVersion 标志）。
    ///
    /// 为何用这个而不是 version-modifier=latest：
    ///   • version-modifier=latest 返回的是"该大版本的最新"，不带「你装的是否最新」语义，
    ///     实测 Java 25 返回 25.0.4.1 而实际最新是 25.0.5.0.3+5（latest=true ≠ latestInFeatureVersion）
    ///   • 按 version 查询直接拿到目标版本的 latestInFeatureVersion 标志
    ///
    /// 开销：约 640 字节/次，不涉及任何缓存。
    /// </summary>
    public async Task<VersionStatus?> CheckVersionAsync(
        int feature, string version, string bundleType, CancellationToken ct = default)
    {
        var url = "https://api.bell-sw.com/v1/liberica/releases" +
                  $"?version-feature={feature}&version={Uri.EscapeDataString(version)}" +
                  $"&os=windows&arch=x86&bitness=64&package-type=zip&bundle-type={bundleType}";

        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        var list = await JsonSerializer.DeserializeAsync<List<LibericaRelease>>(s, cancellationToken: ct) ?? new();

        var hit = list.FirstOrDefault(r => r.Version == version) ?? list.FirstOrDefault();
        if (hit == null) return null;

        return new VersionStatus
        {
            Version = version,
            FeatureVersion = feature,
            IsLatestInFeature = hit.LatestInFeatureVersion,
            IsLatestOverall = hit.Latest,
            IsLts = hit.Lts,
            Variant = hit.Variant,
            FileName = hit.FileName,
            Sha1 = hit.Sha1,
            Size = hit.Size,
        };
    }

    /// <summary>
    /// 查询某大版本的最新构建（用于「快速安装」按钮）。
    /// 官方参数 version-modifier=latest，约 1.2 KB。
    /// </summary>
    public async Task<LibericaRelease?> GetLatestAsync(int feature, string bundleType, CancellationToken ct = default)
    {
        var url = "https://api.bell-sw.com/v1/liberica/releases" +
                  $"?version-feature={feature}&version-modifier=latest" +
                  $"&os=windows&arch=x86&bitness=64&package-type=zip&bundle-type={bundleType}";

        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        var list = await JsonSerializer.DeserializeAsync<List<LibericaRelease>>(s, cancellationToken: ct) ?? new();

        // 优先取 latestInFeatureVersion=true 的那条
        return list.Where(r => r.LatestInFeatureVersion).OrderByDescending(r => BuildKey(r.Version)).FirstOrDefault()
            ?? list.OrderByDescending(r => BuildKey(r.Version)).FirstOrDefault();
    }

    public async Task DownloadParallelAsync(
        string url, string destFile, long expectedSize, string expectedSha1,
        IProgress<(long done, long total)>? progress, CancellationToken ct, int segments = 6)
    {
        using var probe = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        probe.EnsureSuccessStatusCode();

        var total = probe.Content.Headers.ContentLength ?? expectedSize;
        var acceptRanges = probe.Headers.AcceptRanges.Any(r => r.Equals("bytes", StringComparison.OrdinalIgnoreCase));

        if (!acceptRanges || total <= 0 || segments <= 1)
            await DownloadSingleAsync(url, destFile, total, progress, ct);
        else
        {
            try { await DownloadSegmentedAsync(url, destFile, total, progress, ct, segments); }
            catch (OperationCanceledException) { throw; }
            catch { await DownloadSingleAsync(url, destFile, total, progress, ct); }
        }

        if (!string.IsNullOrEmpty(expectedSha1))
        {
            var actual = await Task.Run(() => HashFile(destFile), ct);
            if (!string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"SHA1 校验失败：期望 {expectedSha1}，实际 {actual}");
        }
    }

    private async Task DownloadSegmentedAsync(string url, string destFile, long total,
        IProgress<(long done, long total)>? progress, CancellationToken ct, int segments)
    {
        var partSize = total / segments;
        using var fs = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.Write, 1 << 20, true);
        fs.SetLength(total);
        long written = 0;
        var sync = new object();
        var tasks = new List<Task>();

        for (var i = 0; i < segments; i++)
        {
            var start = i * partSize;
            var end = i == segments - 1 ? total - 1 : start + partSize - 1;
            tasks.Add(Task.Run(async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[1 << 16];
                long pos = start;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    lock (sync) { fs.Position = pos; fs.Write(buffer, 0, read); }
                    pos += read;
                    long done;
                    lock (sync) { written += read; done = written; }
                    progress?.Report((done, total));
                }
            }, ct));
        }
        await Task.WhenAll(tasks);
    }

    private async Task DownloadSingleAsync(string url, string destFile, long total,
        IProgress<(long done, long total)>? progress, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var len = resp.Content.Headers.ContentLength ?? total;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        using var dst = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
        var buffer = new byte[1 << 20];
        long done = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            progress?.Report((done, len));
        }
    }

    private static string HashFile(string path)
    {
        using var sha = System.Security.Cryptography.SHA1.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }
}