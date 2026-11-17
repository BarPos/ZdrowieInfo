using ZdrowieInfo.Server.Sources;
using ZdrowieInfo.Server.Sources.Scheduling;

namespace ZdrowieInfo.Server.Tests.Sources;

public class SourceScheduleTests
{
    private static readonly TimeOnly Five = new(5, 0);

    private static DateTimeOffset Warsaw(int y, int m, int d, int h, int min = 0)
    {
        var local = new DateTime(y, m, d, h, min, 0);
        return new DateTimeOffset(local, Common.WarsawClock.Zone.GetUtcOffset(local)).ToUniversalTime();
    }

    [Theory]
    [InlineData(2026, 11, 2, new[] { SourceGroup.Daily, SourceGroup.Weekly })] // poniedziałek
    [InlineData(2026, 11, 4, new[] { SourceGroup.Daily })]
    [InlineData(2026, 12, 1, new[] { SourceGroup.Daily, SourceGroup.Monthly })] // grudzień parzysty
    [InlineData(2027, 3, 1, new[] { SourceGroup.Daily, SourceGroup.Weekly, SourceGroup.Monthly, SourceGroup.Bimonthly })] // pon. 1 marca
    public void Groups_due_on_date(int y, int m, int d, SourceGroup[] expected) =>
        Assert.Equal(expected, SourceSchedule.GroupsDueOn(new DateOnly(y, m, d)));

    [Fact]
    public void Before_start_time_previous_day_counts()
    {
        Assert.Equal(Warsaw(2026, 11, 16, 5), SourceSchedule.LastSlotUtc(SourceGroup.Daily, Warsaw(2026, 11, 17, 4, 59), Five));
        Assert.Equal(Warsaw(2026, 11, 17, 5), SourceSchedule.LastSlotUtc(SourceGroup.Daily, Warsaw(2026, 11, 17, 5, 0), Five));
    }

    [Fact]
    public void Weekly_monthly_and_bimonthly_slots()
    {
        var now = Warsaw(2026, 12, 9, 12); // środa
        Assert.Equal(Warsaw(2026, 12, 7, 5), SourceSchedule.LastSlotUtc(SourceGroup.Weekly, now, Five));
        Assert.Equal(Warsaw(2026, 12, 1, 5), SourceSchedule.LastSlotUtc(SourceGroup.Monthly, now, Five));
        Assert.Equal(Warsaw(2026, 11, 1, 5), SourceSchedule.LastSlotUtc(SourceGroup.Bimonthly, now, Five));
    }

    [Fact]
    public void Slots_follow_daylight_saving_time()
    {
        // Ostatnia niedziela marca 2027 (28.03): 5:00 to już czas letni (UTC+2).
        Assert.Equal(new DateTimeOffset(2027, 3, 28, 3, 0, 0, TimeSpan.Zero), SourceSchedule.LastSlotUtc(SourceGroup.Daily, Warsaw(2027, 3, 28, 6), Five));
        Assert.Equal(new DateTimeOffset(2027, 3, 27, 4, 0, 0, TimeSpan.Zero), SourceSchedule.LastSlotUtc(SourceGroup.Daily, Warsaw(2027, 3, 27, 6), Five));
    }
}
