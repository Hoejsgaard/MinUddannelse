using NCrontab;

namespace MinUddannelse.Scheduling;

// Cron expressions in this system are authored in Copenhagen local time.
// NCrontab is timezone-unaware, so we must convert UTC <-> local around it.
internal static class CronTimeZoneHelper
{
    internal static readonly TimeZoneInfo CopenhagenTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");

    internal static DateTime? GetNextRunTimeUtc(string cronExpression, DateTime fromTimeUtc)
    {
        var schedule = CrontabSchedule.Parse(cronExpression);
        var fromTimeLocal = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(fromTimeUtc, DateTimeKind.Utc), CopenhagenTimeZone);
        var nextRunLocal = schedule.GetNextOccurrence(fromTimeLocal);

        // Spring-forward gap: the computed local time doesn't exist (e.g., 02:30 on DST start).
        // Skip past the gap and let NCrontab find the next valid occurrence.
        if (CopenhagenTimeZone.IsInvalidTime(nextRunLocal))
        {
            nextRunLocal = schedule.GetNextOccurrence(nextRunLocal.AddHours(1));
        }

        // Force Unspecified kind to avoid ArgumentException in ConvertTimeToUtc
        // when the system local zone happens to match Copenhagen.
        // For ambiguous times (fall-back), ConvertTimeToUtc assumes standard time.
        nextRunLocal = DateTime.SpecifyKind(nextRunLocal, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(nextRunLocal, CopenhagenTimeZone);
    }
}
