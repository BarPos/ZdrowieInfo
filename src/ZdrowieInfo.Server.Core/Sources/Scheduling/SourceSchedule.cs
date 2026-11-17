using ZdrowieInfo.Server.Common;

namespace ZdrowieInfo.Server.Sources.Scheduling;

/// <summary>Terminy grup: codziennie; poniedziałek; 1. dzień miesiąca; 1. dzień miesiąca nieparzystego – o godzinie startu.</summary>
public static class SourceSchedule
{
    public static bool IsDueOn(SourceGroup group, DateOnly date) => group switch
    {
        SourceGroup.Daily => true,
        SourceGroup.Weekly => date.DayOfWeek == DayOfWeek.Monday,
        SourceGroup.Monthly => date.Day == 1,
        SourceGroup.Bimonthly => date.Day == 1 && date.Month % 2 == 1,
        _ => false,
    };

    public static IReadOnlyList<SourceGroup> GroupsDueOn(DateOnly date) =>
        Enum.GetValues<SourceGroup>().Where(g => IsDueOn(g, date)).ToList();

    /// <summary>
    /// Ostatni planowy termin grupy nie późniejszy niż <paramref name="nowUtc"/>. Źródło jest należne, gdy jego ostatnie
    /// udane sprawdzenie jest starsze – dzięki temu przerwa serwera 1. dnia miesiąca nie gubi grupy miesięcznej.
    /// </summary>
    public static DateTimeOffset LastSlotUtc(SourceGroup group, DateTimeOffset nowUtc, TimeOnly startTime)
    {
        var local = WarsawClock.ToLocal(nowUtc);
        var date = DateOnly.FromDateTime(local);
        if (TimeOnly.FromDateTime(local) < startTime) date = date.AddDays(-1);

        while (!IsDueOn(group, date)) date = date.AddDays(-1);

        var slotLocal = date.ToDateTime(startTime, DateTimeKind.Unspecified);
        return new DateTimeOffset(slotLocal, WarsawClock.Zone.GetUtcOffset(slotLocal));
    }
}
