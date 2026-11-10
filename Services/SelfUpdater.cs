using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JavaDeck.Services;

public sealed class ReleaseAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("content_type")] public string ContentType { get; set; } = "";
}

public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("body")] public string Body { get; set; } = "";
    [JsonPropertyName("published_at")] public DateTime PublishedAt { get; set; }
    [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("assets")] public List<ReleaseAsset> Assets { get; set; } = new();

    [JsonIgnore] public string VersionText => TagName.TrimStart('v', 'V');
}

public sealed class UpdateCheckResult
{
    public bool HasUpdate { get; set; }
    public string CurrentVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public GitHubRelease? Release { get; set; }
    public ReleaseAsset? Installer { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// 从 GitHub Releases 检查并下载应用自身更新。
/// 仓库公开，无需 token；走 /releases/latest 接口（自动跳过 draft/prerelease）。
/// </summary>
public static class SelfUpdater
{
    public const string Owner = "cfengli007";
    public const string Repo = "JavaDeck";
    public static string RepoUrl => $"https://github.com/{Owner}/{Repo}";
    private static string ApiLatest => $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var h = new HttpClient(NetworkSettings.CreateHandler()) { Timeout = TimeSpan.FromSeconds(20) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("JavaDeck-SelfUpdater/1.0");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    public static string CurrentVersion =>
        typeof(SelfUpdater).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.0.0";

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var result = new UpdateCheckResult { CurrentVersion = CurrentVersion };
        try
        {
            using var resp = await Http.GetAsync(ApiLatest, ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                result.Error = "仓库还没有发布任何 Release";
                return result;
            }
            resp.EnsureSuccessStatusCode();

            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            var rel = await JsonSerializer.DeserializeAsync<GitHubRelease>(s, cancellationToken: ct);
            if (rel == null) { result.Error = "返回数据为空"; return result; }

            result.Release = rel;
            result.LatestVersion = rel.VersionText;

            // 找安装包
            result.Installer = rel.Assets.FirstOrDefault(a =>
                                  a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                              ?? rel.Assets.FirstOrDefault(a =>
                                  a.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase));

            result.HasUpdate = CompareVersion(rel.VersionText, CurrentVersion) > 0;
            return result;
        }
        catch (OperationCanceledException) { result.Error = "检查超时"; return result; }
        catch (Exception ex) { result.Error = ex.Message; return result; }
    }

    /// <summary>版本比较：返回 1 表示 a 比 b 新。</summary>
    public static int CompareVersion(string a, string b)
    {
        static int[] Parse(string s)
        {
            var core = s.TrimStart('v', 'V').Split('-', '+')[0];
            var parts = core.Split('.');
            var r = new int[parts.Length];
            for (var i = 0; i < parts.Length; i++)
                int.TryParse(new string(parts[i].TakeWhile(char.IsDigit).ToArray()), out r[i]);
            return r;
        }

        var x = Parse(a);
        var y = Parse(b);
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var xi = i < x.Length ? x[i] : 0;
            var yi = i < y.Length ? y[i] : 0;
            if (xi != yi) return xi > yi ? 1 : -1;
        }
        return 0;
    }

    /// <summary>下载安装包到临时目录，返回路径。</summary>
    public static async Task<string> DownloadInstallerAsync(
        ReleaseAsset asset, IProgress<(long done, long total)>? progress, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "JavaDeckUpdate");
        Directory.CreateDirectory(dir);

        // 清掉旧文件
        try { foreach (var f in Directory.GetFiles(dir)) File.Delete(f); } catch { }

        var dest = Path.Combine(dir, asset.Name);

        using var resp = await Http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? asset.Size;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);

        var buffer = new byte[1 << 20];
        long done = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            progress?.Report((done, total));
        }

        return dest;
    }
}