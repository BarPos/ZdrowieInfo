namespace ZdrowieInfo.Server.Common;

/// <summary>Czas lokalny Warszawy na podstawie <see cref="TimeProvider"/> (testy podstawiają FakeTimeProvider).</summary>
public sealed class WarsawClock(TimeProvider time)
{
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    public DateTimeOffset UtcNow => time.GetUtcNow();

    public DateTime LocalNow => TimeZoneInfo.ConvertTime(time.GetUtcNow(), Zone).DateTime;

    public DateOnly Today => DateOnly.FromDateTime(LocalNow);

    public static DateTime ToLocal(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Zone).DateTime;
}
