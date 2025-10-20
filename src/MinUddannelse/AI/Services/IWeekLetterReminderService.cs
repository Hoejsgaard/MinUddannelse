using Newtonsoft.Json.Linq;

namespace MinUddannelse.AI.Services;

public interface IWeekLetterReminderService
{
    Task<ReminderExtractionResult> ExtractAndStoreRemindersAsync(
        string childName,
        int weekNumber,
        int year,
        JObject weekLetter,
        string contentHash);
}

public class ReminderExtractionResult
{
    public bool Success { get; set; }
    public int RemindersCreated { get; set; }
    public bool NoRemindersFound { get; set; }
    public bool AlreadyProcessed { get; set; }
    public string? ErrorMessage { get; set; }
    public List<CreatedReminderInfo> CreatedReminders { get; set; } = new();
}

public class CreatedReminderInfo
{
    public string Title { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public string? EventTime { get; set; }
    public bool IsCurrentWeek { get; set; } = false;
    public string? DayOfWeek { get; set; }
}
