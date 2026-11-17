using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace ZdrowieInfo.Server.Tests.Infrastructure;

/// <summary>
/// Zamiast internetu: odpowiedzi ustawione w teście. Każde inne żądanie dostaje 404 i trafia do <see cref="Unexpected"/>
/// (robots.txt domyślnie 404 = brak ograniczeń). Testy nigdy nie odpytują prawdziwych stron.
/// </summary>
public sealed class FakeHttp : HttpMessageHandler
{
    public sealed record Reply(HttpStatusCode Status, string Body = "", string? ETag = null);

    private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, Reply>> _routes = new();
    public ConcurrentBag<string> Requests { get; } = [];
    public ConcurrentBag<string> Unexpected { get; } = [];

    public void Serve(string url, string html, string? etag = null) =>
        _routes[url] = request => etag is not null && request.Headers.IfNoneMatch.Any(t => t.ToString() == etag)
            ? new Reply(HttpStatusCode.NotModified, ETag: etag)
            : new Reply(HttpStatusCode.OK, html, etag);

    public void Fail(string url, HttpStatusCode status) => _routes[url] = _ => new Reply(status);

    public void Route(string url, Func<HttpRequestMessage, Reply> reply) => _routes[url] = reply;

    public int CountRequests(string url) => Requests.Count(r => r == url);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Requests.Add(url);
        Reply reply;
        if (_routes.TryGetValue(url, out var route)) reply = route(request);
        else
        {
            if (!url.EndsWith("/robots.txt", StringComparison.Ordinal)) Unexpected.Add(url);
            reply = new Reply(HttpStatusCode.NotFound);
        }

        var response = new HttpResponseMessage(reply.Status)
        {
            RequestMessage = request,
            Content = new StringContent(reply.Body, Encoding.UTF8, "text/html"),
        };
        if (reply.ETag is not null) response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(reply.ETag);
        return Task.FromResult(response);
    }
}
