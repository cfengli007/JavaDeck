using System.Net;
using System.Net.Http;

namespace JavaDeck.Services;

/// <summary>把用户设置的代理应用到 HttpClient。</summary>
public static class NetworkSettings
{
    public static IWebProxy? Proxy { get; private set; }
    public static int DownloadSegments { get; private set; } = 6;

    public static void Configure(AppSettingsEx s)
    {
        DownloadSegments = Math.Clamp(s.DownloadSegments, 1, 12);

        if (s.UseProxy && !string.IsNullOrWhiteSpace(s.ProxyAddress))
        {
            try
            {
                Proxy = new WebProxy(s.ProxyAddress)
                {
                    BypassProxyOnLocal = s.BypassProxyOnLocal,
                };
            }
            catch { Proxy = null; }
        }
        else Proxy = null;

        LibericaClient.RefreshHandler(Proxy);
    }

    /// <summary>给 HttpClientHandler 套上代理。</summary>
    public static HttpMessageHandler CreateHandler()
    {
        var h = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 8,
        };
        if (Proxy != null) h.Proxy = Proxy;
        return h;
    }
}
