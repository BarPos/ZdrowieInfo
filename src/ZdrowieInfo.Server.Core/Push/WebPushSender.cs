using System.Net;
using System.Text.Json;
using WebPush;

namespace ZdrowieInfo.Server.Push;

/// <summary>Wysyłka Web Push (pakiet WebPush, VAPID, szyfrowanie treści). Bez zewnętrznych usług.</summary>
public sealed class WebPushSender(IHttpClientFactory httpFactory) : IPushSender
{
    public const string HttpClientName = "ZdrowieInfo.Push";

    public async Task<PushSendResult> SendAsync(PushSubscriptionRecord subscription, PushMessage message, VapidKeys keys, CancellationToken ct = default)
    {
        var client = new WebPushClient(httpFactory.CreateClient(HttpClientName));
        var payload = JsonSerializer.Serialize(new { title = message.Title, body = message.Body, url = message.Url, tag = message.Tag });
        try
        {
            await client.SendNotificationAsync(
                new PushSubscription(subscription.Endpoint, subscription.P256dh, subscription.Auth),
                payload,
                new VapidDetails(keys.Subject, keys.PublicKey, keys.PrivateKey),
                ct);
            return new PushSendResult(PushSendStatus.Sent);
        }
        catch (WebPushException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return new PushSendResult(PushSendStatus.Gone, $"HTTP {(int)e.StatusCode}");
        }
        catch (WebPushException e)
        {
            return new PushSendResult(PushSendStatus.Failed, $"HTTP {(int)e.StatusCode}: {e.Message}");
        }
        catch (HttpRequestException e)
        {
            return new PushSendResult(PushSendStatus.Failed, e.Message);
        }
    }
}
