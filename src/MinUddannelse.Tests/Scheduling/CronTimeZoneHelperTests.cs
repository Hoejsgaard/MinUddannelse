using MinUddannelse.Scheduling;
using Xunit;

namespace MinUddannelse.Tests.Scheduling;

public class CronTimeZoneHelperTests
{
    private static readonly TimeZoneInfo Copenhagen = CronTimeZoneHelper.CopenhagenTimeZone;

    [Fact]
    public void GetNextRunTimeUtc_WinterCET_ReturnsMondayAt0545Utc()
    {
        // CET = UTC+1, so 06:45 Copenhagen = 05:45 UTC
        // Starting from a Monday in January (well within CET)
        var fromUtc = new DateTime(2026, 1, 5, 5, 46, 0, DateTimeKind.Utc); // Mon Jan 5, just after 06:46 CET

        var result = CronTimeZoneHelper.GetNextRunTimeUtc("45 6 * * 1", fromUtc);

        Assert.NotNull(result);
        Assert.Equal(DayOfWeek.Monday, TimeZoneInfo.ConvertTimeFromUtc(result.Value, Copenhagen).DayOfWeek);
        // Next Monday 06:45 CET = 05:45 UTC
        Assert.Equal(5, result.Value.Hour);
        Assert.Equal(45, result.Value.Minute);
    }

    [Fact]
    public void GetNextRunTimeUtc_SummerCEST_ReturnsMondayAt0445Utc()
    {
        // CEST = UTC+2, so 06:45 Copenhagen = 04:45 UTC
        // Starting from a Monday in July (well within CEST)
        var fromUtc = new DateTime(2026, 7, 6, 4, 46, 0, DateTimeKind.Utc); // Mon Jul 6, just after 06:46 CEST

        var result = CronTimeZoneHelper.GetNextRunTimeUtc("45 6 * * 1", fromUtc);

        Assert.NotNull(result);
        Assert.Equal(DayOfWeek.Monday, TimeZoneInfo.ConvertTimeFromUtc(result.Value, Copenhagen).DayOfWeek);
        // Next Monday 06:45 CEST = 04:45 UTC
        Assert.Equal(4, result.Value.Hour);
        Assert.Equal(45, result.Value.Minute);
    }

    [Fact]
    public void GetNextRunTimeUtc_CrossDstBoundary_CetToCest_CorrectUtcOffset()
    {
        // LastRun was in CET (winter), next occurrence falls in CEST (summer)
        // DST starts last Sunday of March 2026 = March 29
        // From Monday March 23 (CET), next Monday is March 30 (CEST)
        var fromUtc = new DateTime(2026, 3, 23, 5, 46, 0, DateTimeKind.Utc); // Mon Mar 23, 06:46 CET

        var result = CronTimeZoneHelper.GetNextRunTimeUtc("45 6 * * 1", fromUtc);

        Assert.NotNull(result);
        // March 30 is CEST (UTC+2), so 06:45 CEST = 04:45 UTC
        Assert.Equal(new DateTime(2026, 3, 30, 4, 45, 0, DateTimeKind.Utc), result.Value);
    }

    [Fact]
    public void GetNextRunTimeUtc_SpringForwardGap_SkipsToNextValidOccurrence()
    {
        // DST starts last Sunday of March 2026 = March 29, clocks jump 02:00 -> 03:00
        // Cron for daily at 02:30 — on March 29, 02:30 doesn't exist
        var fromUtc = new DateTime(2026, 3, 28, 1, 31, 0, DateTimeKind.Utc); // Sat Mar 28 02:31 CET, just after 02:30

        var result = CronTimeZoneHelper.GetNextRunTimeUtc("30 2 * * *", fromUtc);

        Assert.NotNull(result);
        var resultLocal = TimeZoneInfo.ConvertTimeFromUtc(result.Value, Copenhagen);
        // March 29 02:30 doesn't exist, so it should skip to March 30 02:30 CEST
        Assert.True(resultLocal.Day >= 30,
            $"Expected day >= 30 (skipping invalid March 29 02:30), got {resultLocal}");
        Assert.Equal(2, resultLocal.Hour);
        Assert.Equal(30, resultLocal.Minute);
    }

    [Fact]
    public void GetNextRunTimeUtc_FallBackAmbiguity_ReturnsStandardTimeInterpretation()
    {
        // DST ends last Sunday of October 2026 = October 25, clocks go 03:00 -> 02:00
        // Cron for daily at 02:30 — on October 25, 02:30 exists twice
        // Standard time (CET) interpretation: 02:30 CET = 01:30 UTC
        var fromUtc = new DateTime(2026, 10, 24, 1, 31, 0, DateTimeKind.Utc); // Sat Oct 24, after 02:30 CEST

        var result = CronTimeZoneHelper.GetNextRunTimeUtc("30 2 * * *", fromUtc);

        Assert.NotNull(result);
        // On Oct 25, ambiguous 02:30 with standard time assumption -> 02:30 CET = 01:30 UTC
        Assert.Equal(25, result.Value.Day);
        Assert.Equal(1, result.Value.Hour);
        Assert.Equal(30, result.Value.Minute);
    }

    [Fact]
    public void GetNextRunTimeUtc_DailyAt0645_ConsistentAcrossSeasons()
    {
        // 06:45 is never in a DST gap or ambiguous window for Copenhagen
        // (transitions happen at 02:00/03:00), so this should always work cleanly
        var winterUtc = new DateTime(2026, 1, 15, 5, 46, 0, DateTimeKind.Utc);
        var summerUtc = new DateTime(2026, 7, 15, 4, 46, 0, DateTimeKind.Utc);

        var winterResult = CronTimeZoneHelper.GetNextRunTimeUtc("45 6 * * *", winterUtc);
        var summerResult = CronTimeZoneHelper.GetNextRunTimeUtc("45 6 * * *", summerUtc);

        Assert.NotNull(winterResult);
        Assert.NotNull(summerResult);

        // Winter: 06:45 CET = 05:45 UTC
        Assert.Equal(5, winterResult.Value.Hour);
        Assert.Equal(45, winterResult.Value.Minute);

        // Summer: 06:45 CEST = 04:45 UTC
        Assert.Equal(4, summerResult.Value.Hour);
        Assert.Equal(45, summerResult.Value.Minute);
    }
}
