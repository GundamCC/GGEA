using GGEA.App.Localization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace GGEA.App.Updates;

/// <summary>更新网络边界：不带凭证、Cookie，响应大小、等待时间和重定向均有界。</summary>
internal sealed class UpdateHttpTransport : IDisposable
{
    public static readonly string[] GitHubHosts = ["api.github.com", "github.com", "raw.githubusercontent.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"];
    private readonly HttpClient _http;

    /// <summary>测试只替换传输 handler，实际解析与信任校验仍执行生产代码。</summary>
    public UpdateHttpTransport(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GGEA-Updater/1.0");
    }

    /// <summary>小响应也受流式大小限制，避免错误页或异常服务器占满内存。</summary>
    public async Task<string> ReadSmallAsync(Uri uri, int maximum, IReadOnlyCollection<string> hosts, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(uri, hosts, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            int count;
            try { count = await stream.ReadAsync(buffer, cancellationToken); }
            catch (IOException ex) { throw new HttpRequestException(L.Text("更新源响应连接中断。"), ex); }
            if (count == 0) break;
            if (memory.Length + count > maximum) throw new InvalidDataException(L.Text("更新响应大小异常。"));
            memory.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    /// <summary>逐跳校验 HTTPS 与来源授权的域名；响应错误不输出可能含临时签名的 URL。</summary>
    public async Task<HttpResponseMessage> GetAsync(Uri uri, IReadOnlyCollection<string> hosts, CancellationToken cancellationToken)
    {
        for (var redirects = 0; redirects < 6; redirects++)
        {
            if (!UpdateSourceCatalog.IsPublicHttps(uri) || !hosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException(L.Text("更新地址不在此来源授权的 HTTPS 域名中。"));
            using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headersTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (uri.Host == "api.github.com")
            {
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            }
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                uri = location is null ? throw new InvalidDataException(L.Text("更新下载重定向缺少地址。")) : new Uri(uri, location);
                continue;
            }
            if (response.IsSuccessStatusCode) return response;
            var status = response.StatusCode;
            response.Dispose();
            throw new HttpRequestException(status switch
            {
                HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => L.Text("更新源暂时限制了请求，请稍后重试。"),
                HttpStatusCode.NotFound => L.Text("尚无可用正式发布，或发布附件暂时不可访问。"),
                _ => L.Format($"更新源请求失败（HTTP {(int)status}），请稍后重试。")
            }, null, status);
        }
        throw new HttpRequestException(L.Text("更新下载重定向次数过多。"));
    }

    /// <summary>所有请求结束后释放连接。</summary>
    public void Dispose() => _http.Dispose();
}
