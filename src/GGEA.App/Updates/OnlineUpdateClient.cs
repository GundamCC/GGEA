using GGEA.App.Localization;
using System.IO;
using System.Net.Http;

namespace GGEA.App.Updates;

/// <summary>GitHub 优先的更新协调器：刷新来源缓存，网络失败时有限切换，取消与校验失败立即停止。</summary>
public sealed class OnlineUpdateClient : IDisposable
{
    private readonly UpdateHttpTransport _http;
    private readonly GitHubUpdateClient _github;
    private readonly UpdateSourceStore _sources;
    private readonly string _installation;
    private readonly Action<UpdateSourceStatus>? _report;

    /// <summary>所有调用入口使用同一安装目录中的来源状态，测试可注入独立目录和传输。</summary>
    public OnlineUpdateClient(string installation, Action<UpdateSourceStatus>? report = null, HttpMessageHandler? handler = null)
    {
        _installation = installation;
        _report = report;
        _sources = new(installation, message => report?.Invoke(new(message, true)));
        _http = new(handler);
        _github = new(_http);
    }

    /// <summary>来源发现与版本检查并行；即使本机已是最新版，也会学习远程新增的备用源。</summary>
    public async Task<UpdateRelease?> CheckAsync(Version current, CancellationToken cancellationToken)
    {
        var discovery = RefreshSourcesAsync(cancellationToken);
        try
        {
            try { return await _github.CheckAsync(current, cancellationToken); }
            catch (Exception ex) when (IsNetworkFailure(ex, cancellationToken))
            {
                _report?.Invoke(new(L.Text("GitHub 暂时不可访问，正在尝试备用更新源。"), true));
                await discovery;
                var sources = _sources.Load();
                var reachedMirror = false;
                foreach (var source in sources)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var update = await ReadMirrorAsync(source, cancellationToken);
                        _report?.Invoke(new(L.Format($"已使用备用更新源：{source.Name}。")));
                        reachedMirror = true;
                        if (update.Version > current) return update;
                    }
                    catch (Exception error) when (IsNetworkFailure(error, cancellationToken))
                    { _report?.Invoke(new(L.Format($"备用源 {source.Name} 暂时不可访问，继续尝试下一来源。"), true)); }
                }
                if (reachedMirror) return null;
                throw new HttpRequestException(sources.Length == 0
                    ? L.Text("GitHub 无法连接，尚无可用的备用源。请稍后重试，或从官方发布页手动下载。")
                    : L.Text("GitHub 与已配置的备用更新源均无法连接，请稍后重试。"), ex);
            }
        }
        finally { await discovery; }
    }

    /// <summary>下载失败后只选择完全相同版本与大小的镜像；已知摘要必须一致，不能偷偷换包或升级版本。</summary>
    public async Task<string> DownloadAsync(UpdateRelease update, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var downloader = new UpdatePackageDownloader(_http, _report);
        var expectedHash = update.Sha256;
        try
        {
            expectedHash = await downloader.ResolveHashAsync(update, cancellationToken);
            return await downloader.DownloadAsync(update with { Sha256 = expectedHash }, _installation, progress, cancellationToken);
        }
        catch (Exception ex) when (IsNetworkFailure(ex, cancellationToken))
        {
            _report?.Invoke(new(L.Format($"{update.SourceName} 下载连接失败，正在尝试同版本备用包。"), true));
            foreach (var source in _sources.Load().Where(s => s.Id != update.SourceId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var mirror = await ReadMirrorAsync(source, cancellationToken);
                    if (mirror.Version != update.Version || mirror.PackageName != update.PackageName || mirror.PackageSize != update.PackageSize)
                    {
                        _report?.Invoke(new(L.Format($"备用源 {source.Name} 尚无匹配版本，已跳过。")));
                        continue;
                    }
                    if (expectedHash is not null && !expectedHash.Equals(mirror.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(L.Text("备用源与原来源的同版本包摘要不一致，已停止更新。"));
                    expectedHash ??= mirror.Sha256;
                    _report?.Invoke(new(L.Format($"正在从 {source.Name} 下载；将重新校验完整包。")));
                    return await downloader.DownloadAsync(mirror, _installation, progress, cancellationToken);
                }
                catch (Exception error) when (IsNetworkFailure(error, cancellationToken))
                { _report?.Invoke(new(L.Format($"备用源 {source.Name} 下载连接失败，继续尝试下一来源。"), true)); }
            }
            throw new HttpRequestException(L.Text("下载源无法连接，且没有可用的同版本备用包。请稍后重试或从官方发布页手动下载。"), ex);
        }
    }

    /// <summary>配置入口未发布、离线或格式错误都不覆盖缓存，也不影响 GitHub 正常更新。</summary>
    private async Task RefreshSourcesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var json = await _http.ReadSmallAsync(new Uri(UpdateSourceStore.DiscoveryUrl),
                UpdateSourceCatalog.MaximumBytes, UpdateHttpTransport.GitHubHosts, timeout.Token);
            _sources.SaveCache(json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException
                                  or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            _report?.Invoke(new(L.Format($"备用源配置暂未刷新（{ex.GetType().Name}），继续使用已配置的来源。"),
                ex is not HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound }));
        }
    }

    /// <summary>每个备用版本清单有独立超时，读取范围与下载相同；不跨来源扩大域名授权。</summary>
    private async Task<UpdateRelease> ReadMirrorAsync(UpdateSource source, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var json = await _http.ReadSmallAsync(new Uri(source.ManifestUrl), 1024 * 1024, source.AllowedHosts, timeout.Token);
        return UpdateSourceCatalog.ParseRelease(json, source);
    }

    /// <summary>用户取消、文件错误和校验错误不能触发重试；只有连接或请求超时允许换源。</summary>
    private static bool IsNetworkFailure(Exception error, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && error is HttpRequestException or OperationCanceledException;

    /// <summary>调用完成后释放共用连接。</summary>
    public void Dispose() => _http.Dispose();
}
