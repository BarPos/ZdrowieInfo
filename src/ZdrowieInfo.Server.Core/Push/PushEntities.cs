namespace ZdrowieInfo.Server.Push;

/// <summary>Subskrypcja Web Push jednego urządzenia (tabela <c>push_subscriptions</c>).</summary>
public sealed class PushSubscriptionRecord
{
    public long Id { get; set; }
    public required string UserId { get; set; }
    public required string Endpoint { get; set; }
    public required string P256dh { get; set; }
    public required string Auth { get; set; }

    /// <summary>Krótki opis urządzenia (z User-Agenta), żeby rozpoznać je na liście.</summary>
    public string? Device { get; set; }

    /// <summary>Adres aplikacji, na którym zarejestrowano service workera (subskrypcje są związane z domeną).</summary>
    public required string Origin { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
}

/// <summary>Treść powiadomienia – format odczytywany przez service-worker.js.</summary>
public sealed record PushMessage(string Title, string Body, string Url, string? Tag = null);

public enum PushSendStatus { Sent, Gone, Failed }

public sealed record PushSendResult(PushSendStatus Status, string? Error = null);

/// <summary>Wysyłka do jednej subskrypcji (Web Push z VAPID). W testach podmieniana.</summary>
public interface IPushSender
{
    Task<PushSendResult> SendAsync(PushSubscriptionRecord subscription, PushMessage message, VapidKeys keys, CancellationToken ct = default);
}

/// <summary>Klucze VAPID zapisane w app_settings (zaszyfrowane) razem z adresem, dla którego powstały.</summary>
public sealed record VapidKeys(string PublicKey, string PrivateKey, string PublicUrl, string Subject);
