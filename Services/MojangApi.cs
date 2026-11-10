using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JavaDeck.Services;

public sealed class McVersion
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Url { get; set; } = "";
    public int JavaMajor { get; set; }
    public string JavaSource => JavaMajor == 0 ? "?" : "Java " + JavaMajor;
}

public sealed class MojangApi
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<List<McVersion>> GetReleasesAsync(IProgress <double>? progress, CancellationToken ct)
    {
        using var resp = await Http.GetAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", ct);
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        var root = await JsonSerializer.DeserializeAsync<ManifestRoot>(s, cancellationToken: ct);
        var all = (root?.Versions ?? new List<RawVersion>())
            .Where(v => v.Type == "release")
            .Select(v => new McVersion { Id = v.Id, Type = v.Type, Url = v.Url, JavaMajor = 0 })
            .ToList();

        var sample = all.Take(80).ToList();
        var done = 0;
        using var gate = new SemaphoreSlim(8);
        var tasks = sample.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var r = await Http.GetAsync(item.Url, ct);
                if (!r.IsSuccessStatusCode) return;
                await using var rs = await r.Content.ReadAsStreamAsync(ct);
                var detail = await JsonSerializer.DeserializeAsync<VersionDetail>(rs, cancellationToken: ct);
                item.JavaMajor = detail?.JavaVersion?.MajorVersion ?? 0;
            }
            catch { }
            finally
            {
                gate.Release();
                progress?.Report(Interlocked.Increment(ref done) / (double)sample.Count);
            }
        });
        await Task.WhenAll(tasks);
        return all;
    }

    private sealed class ManifestRoot
    {
        [JsonPropertyName("versions")] public List<RawVersion>? Versions { get; set; }
    }

    private sealed class RawVersion
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
    }

    private sealed class VersionDetail
    {
        [JsonPropertyName("javaVersion")] public JavaVer? JavaVersion { get; set; }
    }

    private sealed class JavaVer
    {
        [JsonPropertyName("majorVersion")] public int MajorVersion { get; set; }
        [JsonPropertyName("component")] public string Component { get; set; } = "";
    }
}
