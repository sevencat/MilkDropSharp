using System.Net;
using System.Text;

namespace MilkDrop.ShaderToy.Import;

/// <summary>单个资源抓取失败（带人话描述）。</summary>
internal sealed class DownloadException(string message) : Exception(message);

/// <summary>
/// 资源抓取的公共小工具：带 UA/Referer 的 GET + 退避重试、站点路径拼 URL、文件名清洗。
/// </summary>
/// <remarks>
/// 下载整件作品（<see cref="ShaderToyDownloader"/>）和"补齐缺失资源"
/// （<see cref="MissingResourceFetcher"/>）走同一套逻辑，避免两处 UA/重试策略跑偏。
/// </remarks>
internal static class AssetHttp
{
    /// <summary>429 与 5xx 退避重试，其余 4xx 直接失败。</summary>
    public static async Task<byte[]> GetBytesAsync(
        HttpClient http,
        string url,
        DownloadOptions options,
        CancellationToken cancellationToken)
    {
        DownloadException? last = null;

        for (var attempt = 0; attempt <= options.MaxRetries; attempt++)
        {
            if (attempt > 0 && options.RetryDelay > TimeSpan.Zero)
            {
                await Task.Delay(options.RetryDelay * attempt, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                using HttpResponseMessage response = await http
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                }

                var error = new DownloadException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}（{Shorten(url)}）");

                bool retryable = response.StatusCode == HttpStatusCode.TooManyRequests
                                 || (int)response.StatusCode >= 500;
                if (!retryable)
                {
                    throw error;
                }

                last = error;
            }
            catch (HttpRequestException ex)
            {
                last = new DownloadException($"网络错误：{ex.Message}（{Shorten(url)}）");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                last = new DownloadException($"请求超时（{Shorten(url)}）");
            }
        }

        throw last ?? new DownloadException("下载失败：" + Shorten(url));
    }

    /// <summary>
    /// 把 JSON 里的路径换成可下载的绝对 URL：
    /// 站点绝对路径（<c>/media/a/xxx.png</c>）拼上 <see cref="ShaderToyDownloader.SiteBase"/>，http(s) 原样返回；
    /// 本地相对路径返回 false（不需要联网）。
    /// </summary>
    public static bool TryToAbsoluteUrl(string path, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? url)
    {
        url = null;

        if (path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal))
        {
            url = ShaderToyDownloader.SiteBase + path;
            return true;
        }

        if (Uri.TryCreate(path, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            url = uri.ToString();
            return true;
        }

        return false;
    }

    /// <summary>从 URL 里取文件名并去掉 Windows 非法字符（站点上是 hash，一般不会命中）。</summary>
    public static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);

        foreach (char c in name)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        return builder.ToString().Trim().TrimEnd('.');
    }

    /// <summary>日志里的 URL 截断，避免控制台被长链接糊住。</summary>
    public static string Shorten(string url) => url.Length <= 120 ? url : url[..117] + "…";
}
