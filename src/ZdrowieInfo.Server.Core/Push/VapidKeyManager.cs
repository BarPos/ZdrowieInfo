using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZdrowieInfo.Server.Common;
using ZdrowieInfo.Server.Data;
using ZdrowieInfo.Server.Settings;

namespace ZdrowieInfo.Server.Push;

/// <summary>
/// Klucze VAPID: generowane przy pierwszym starcie i przy każdej zmianie <c>ZdrowieInfo:PublicUrl</c>. Same klucze nie są
/// związane z domeną, ale subskrypcje tak (origin service workera) – po zmianie adresu i tak nie zadziałają, więc są usuwane.
/// </summary>
public sealed class VapidKeyManager(
    AppSettingsStore settings,
    IDbContextFactory<ZdrowieInfoDbContext> dbFactory,
    IOptions<ZdrowieInfoOptions> app,
    IOptions<Sources.SourcesOptions> sources,
    ILogger<VapidKeyManager> logger)
{
    private const string Key = "push.vapid";
    private VapidKeys? _cached;

    public async Task<VapidKeys> EnsureAsync(CancellationToken ct = default)
    {
        var publicUrl = app.Value.PublicUri.GetLeftPart(UriPartial.Authority);
        var stored = await settings.GetAsync<VapidKeys>(Key, ct);
        if (stored is not null && stored.PublicUrl == publicUrl)
            return _cached = stored;

        var generated = WebPush.VapidHelper.GenerateVapidKeys();
        var keys = new VapidKeys(generated.PublicKey, generated.PrivateKey, publicUrl, Subject(publicUrl));
        await settings.SetAsync(Key, keys, isProtected: true, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var removed = await db.PushSubscriptions.ExecuteDeleteAsync(ct);
        if (stored is null)
            logger.LogInformation("Wygenerowano klucze VAPID dla {Url}.", publicUrl);
        else
            logger.LogWarning("Zmiana adresu aplikacji {Old} → {New}: nowe klucze VAPID, usunięto {Count} subskrypcji push. " +
                              "Zainstaluj aplikację z nowego adresu i włącz powiadomienia ponownie.", stored.PublicUrl, publicUrl, removed);
        return _cached = keys;
    }

    public async Task<VapidKeys> GetAsync(CancellationToken ct = default) => _cached ?? await EnsureAsync(ct);

    /// <summary>Temat VAPID: adres kontaktowy, a gdy go nie ma – adres aplikacji (https) albo zastępczy mailto (lokalnie).</summary>
    private string Subject(string publicUrl)
    {
        var contact = sources.Value.ContactEmail;
        if (!string.IsNullOrWhiteSpace(contact)) return $"mailto:{contact}";
        return publicUrl.StartsWith("https://", StringComparison.Ordinal) ? publicUrl : "mailto:zdrowieinfo@localhost";
    }
}
