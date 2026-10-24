using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace MilkDrop.ShaderToy.Tests.Import;

/// <summary>
/// 假 HTTP：按 URL 返回固定内容/响应序列，没登记的返回 404，并统计每个 URL 的命中次数。
/// </summary>
/// <remarks>下载与"补齐资源"两边共用（都不联网）。</remarks>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<int, HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>每个 URL 被请求了几次（测重试/增量下载用）。</summary>
    public Dictionary<string, int> Hits { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FakeHttpHandler Text(string url, string content) =>
        Map(url, _ => Ok(Encoding.UTF8.GetBytes(content), "text/html; charset=utf-8"));

    public FakeHttpHandler Bytes(string url, byte[] content, string contentType = "image/png") =>
        Map(url, _ => Ok(content, contentType));

    /// <summary>按调用次数依次返回响应（最后一次之后一直重复它）——用来测 429 重试。</summary>
    public FakeHttpHandler Sequence(string url, params HttpStatusCode[] statuses) =>
        Map(url, call => new HttpResponseMessage(
            statuses[Math.Min(call, statuses.Length - 1)])
        {
            Content = new ByteArrayContent(statuses[Math.Min(call, statuses.Length - 1)] == HttpStatusCode.OK
                ? [1, 2, 3]
                : []),
        });

    private FakeHttpHandler Map(string url, Func<int, HttpResponseMessage> factory)
    {
        _routes[url] = factory;
        return this;
    }

    private static HttpResponseMessage Ok(byte[] content, string contentType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content),
        };

        response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType.Split(';')[0]);
        return response;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string url = request.RequestUri!.ToString();
        int call = Hits.GetValueOrDefault(url);
        Hits[url] = call + 1;

        HttpResponseMessage response = _routes.TryGetValue(url, out Func<int, HttpResponseMessage>? factory)
            ? factory(call)
            : new HttpResponseMessage(HttpStatusCode.NotFound);

        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}
