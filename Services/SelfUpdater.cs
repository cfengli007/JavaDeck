using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace JavaDeck.Services;

public sealed class GitHubRelease
{
    public string TagName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime? PublishedAt { get; set; }
    public string HtmlUrl { get; set; } = "";
    public List<ReleaseAsset> Assets { get; set; } = new();
}

public sealed class ReleaseAsset
{
    public string Name { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public long Size { get; set; }
}

public sealed class UpdateCheckResult
{
    public bool HasUpdate { get; set; }
    public string CurrentVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public GitHubRelease? Release { get; set; }
    public ReleaseAsset? Installer { get; set; }
    public string? Error { get; set; }
    public string Source { get; set; } = "";
}

/// <summary>
/// 应用自更新检查。
///
/// 背景：GitHub REST API 未认证限流 60 次/小时/IP，很容易 403。
/// 因此改为三级降级，全部不依赖 REST API：
///
///   1) GET /releases/latest               -> 302，从 Location 解析 tag
///   2) GET /releases/expanded_assets/<tag> -> HTML 片段，正则取真实资产文件名
///   3) GET /releases.atom                  -> Atom 订阅源，含版本号 + 更新说明
///
/// 实测三者都返回 200/302，无限流。
/// </summary>
public static class SelfUpdater
{
    public const string Owner = "cfengli007";
    public const string Repo = "JavaDeck";
    public static string RepoUrl => $"https://github.com/{Owner}/{Repo}";
    private static string BaseUrl => $"https://github.com/{Owner}/{Repo}";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = false,
        };
        var h = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("JavaDeck-SelfUpdater/2.0");
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
            var tag = await GetLatestTagAsync(ct);
            if (tag == null)
            {
                result.Error = "无法获取最新版本号（网络不可达或仓库无 Release）";
                return result;
            }

            result.LatestVersion = NormalizeTag(tag);
            result.Source = "redirect";
            result.Release = new GitHubRelease
            {
                TagName = tag,
                Name = "JavaDeck " + result.LatestVersion,
                HtmlUrl = $"{BaseUrl}/releases/tag/{tag}",
            };

            try { await FillAssetsAsync(tag, result.Release, ct); } catch { }
            try { await FillFromAtomAsync(result.Release, ct); } catch { }

            if (result.Release.Assets.Count == 0)
            {
                var guessName = $"JavaDeck-Setup-{result.LatestVersion}-win-x64.exe";
                result.Release.Assets.Add(new ReleaseAsset
                {
                    Name = guessName,
                    DownloadUrl = $"{BaseUrl}/releases/download/{tag}/{guessName}",
                });
            }

            result.Installer = result.Release.Assets.FirstOrDefault(a =>
                                  a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

            result.HasUpdate = CompareVersion(result.LatestVersion, CurrentVersion) > 0;
            return result;
        }
        catch (OperationCanceledException) { result.Error = "检查超时"; return result; }
        catch (Exception ex) { result.Error = ex.Message; return result; }
    }

    private static async Task<string?> GetLatestTagAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/releases/latest");
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        var loc = resp.Headers.Location?.ToString();
        if (!string.IsNullOrEmpty(loc))
        {
            var m = Regex.Match(loc, @"/releases/tag/([^/?#]+)");
            if (m.Success) return Uri.UnescapeDataString(m.Groups[1].Value);
        }

        if (resp.StatusCode == HttpStatusCode.OK && resp.RequestMessage?.RequestUri != null)
        {
            var finalUrl = resp.RequestMessage.RequestUri.ToString();
            var m2 = Regex.Match(finalUrl, @"/releases/tag/([^/?#]+)");
            if (m2.Success) return Uri.UnescapeDataString(m2.Groups[1].Value);
        }

        return null;
    }

    private static async Task FillAssetsAsync(string tag, GitHubRelease rel, CancellationToken ct)
    {
        using var resp = await Http.GetAsync($"{BaseUrl}/releases/expanded_assets/{tag}", ct);
        if (!resp.IsSuccessStatusCode) return;

        var html = await resp.Content.ReadAsStringAsync(ct);

        foreach (Match m in Regex.Matches(html, @"href=""[^""]*?/releases/download/[^""/]+/([^""]+)"""))
        {
            var raw = m.Groups[1].Value;
            var name = Uri.UnescapeDataString(raw).Split('?')[0];
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (rel.Assets.Any(a => a.Name == name)) continue;

            rel.Assets.Add(new ReleaseAsset
            {
                Name = name,
                DownloadUrl = $"{BaseUrl}/releases/download/{tag}/{Uri.EscapeDataString(name)}",
            });
        }
    }

    private static async Task FillFromAtomAsync(GitHubRelease rel, CancellationToken ct)
    {
        using var resp = await Http.GetAsync($"{BaseUrl}/releases.atom", ct);
        if (!resp.IsSuccessStatusCode) return;

        var xml = await resp.Content.ReadAsStringAsync(ct);

        var entries = Regex.Matches(xml, @"<entry>([\s\S]*?)</entry>");
        foreach (Match e in entries)
        {
            var block = e.Groups[1].Value;
            var idm = Regex.Match(block, @"<id>[^<]*/([^/<\s]+)</id>");
            if (!idm.Success || NormalizeTag(idm.Groups[1].Value) != NormalizeTag(rel.TagName)) continue;

            var title = Regex.Match(block, @"<title>([\s\S]*?)</title>");
            if (title.Success) rel.Name = WebUtility.HtmlDecode(title.Groups[1].Value.Trim());

            var upd = Regex.Match(block, @"<updated>([^<]+)</updated>");
            if (upd.Success && DateTime.TryParse(upd.Groups[1].Value, out var dt)) rel.PublishedAt = dt;

            var content = Regex.Match(block, @"<content[^>]*>([\s\S]*?)</content>");
            if (content.Success)
            {
                var htmlText = WebUtility.HtmlDecode(content.Groups[1].Value);
                rel.Body = StripHtml(htmlText);
            }
            return;
        }
    }

    private static string StripHtml(string html)
    {
        var s = html;
        s = Regex.Replace(s, @"<h[1-6][^>]*>", "\n## ");
        s = Regex.Replace(s, @"</h[1-6]>", "\n");
        s = Regex.Replace(s, @"<li[^>]*>", "\n- ");
        s = Regex.Replace(s, @"</li>", "");
        s = Regex.Replace(s, @"<br\s*/?>", "\n");
        s = Regex.Replace(s, @"</p>", "\n\n");
        s = Regex.Replace(s, @"<code[^>]*>|</code>", "`");
        s = Regex.Replace(s, @"</?strong[^>]*>", "**");
        s = Regex.Replace(s, @"<[^>]+>", "");
        s = WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    private static string NormalizeTag(string tag) => tag.TrimStart('v', 'V');

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

    public static async Task<string> DownloadInstallerAsync(
        ReleaseAsset asset, IProgress<(long done, long total)>? progress, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "JavaDeckUpdate");
        Directory.CreateDirectory(dir);
        try { foreach (var f in Directory.GetFiles(dir)) File.Delete(f); } catch { }

        var dest = Path.Combine(dir, asset.Name);

        using var follow = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AllowAutoRedirect = true,
        })
        { Timeout = TimeSpan.FromMinutes(10) };
        follow.DefaultRequestHeaders.UserAgent.ParseAdd("JavaDeck-SelfUpdater/2.0");

        using var resp = await follow.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
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