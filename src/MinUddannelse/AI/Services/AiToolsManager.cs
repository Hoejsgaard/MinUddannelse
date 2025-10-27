using Microsoft.Extensions.Logging;
using MinUddannelse;
using System.ComponentModel;
using MinUddannelse.Configuration;
using MinUddannelse.Models;
using MinUddannelse.Repositories.DTOs;
using MinUddannelse.Repositories;

namespace MinUddannelse.AI.Services;

public class AiToolsManager : IAiToolsManager
{
    private readonly IReminderRepository _reminderRepository;
    private readonly IScheduledTaskRepository _scheduledTaskRepository;
    private readonly WeekLetterCache _dataService;
    private readonly Config _config;
    private readonly ILogger _logger;

    public AiToolsManager(IReminderRepository reminderRepository, IScheduledTaskRepository scheduledTaskRepository, WeekLetterCache dataService, Config config, ILoggerFactory loggerFactory)
    {
        _reminderRepository = reminderRepository;
        _scheduledTaskRepository = scheduledTaskRepository;
        _dataService = dataService;
        _config = config;
        _logger = loggerFactory.CreateLogger<AiToolsManager>();
    }

    public async Task<string> CreateReminderAsync(string description, string dateTime, string? childName = null)
    {
        try
        {
            if (!DateTime.TryParse(dateTime, out var parsedDateTime))
            {
                return "Error: Invalid date format. Please use 'yyyy-MM-dd HH:mm' format.";
            }

            var date = DateOnly.FromDateTime(parsedDateTime);
            var time = TimeOnly.FromDateTime(parsedDateTime);

            var reminderId = await _reminderRepository.AddReminderAsync(description, date, time, childName);

            var childInfo = string.IsNullOrEmpty(childName) ? "" : $" for {childName}";
            _logger.LogInformation("Created reminder: {Description} at {DateTime}{ChildInfo}", description, parsedDateTime, childInfo);

            return $"✅ Reminder created: '{description}' for {parsedDateTime:yyyy-MM-dd HH:mm}{childInfo}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create reminder");
            return "❌ Failed to create reminder. Please try again.";
        }
    }

    public async Task<string> CreateRecurringReminderAsync(string description, string dateTime, string recurrenceType, int dayOfWeek, string? childName = null)
    {
        try
        {
            TimeOnly time;

            // For recurring reminders, use DefaultOnDateReminderTime if no specific time is provided
            // Check if dateTime is null/empty or if it parses to midnight (00:00)
            if (string.IsNullOrWhiteSpace(dateTime) ||
                !DateTime.TryParse(dateTime, out var parsedDateTime) ||
                (parsedDateTime.Hour == 0 && parsedDateTime.Minute == 0))
            {
                // Use the default reminder time from configuration
                if (TimeOnly.TryParse(_config.Scheduling.DefaultOnDateReminderTime, out var defaultTime))
                {
                    time = defaultTime;
                    _logger.LogInformation("Using default reminder time {DefaultTime} for recurring reminder (dateTime: '{DateTime}')", time, dateTime);
                }
                else
                {
                    time = new TimeOnly(6, 45); // Fallback to 06:45 if config is invalid
                    _logger.LogWarning("Could not parse DefaultOnDateReminderTime, using fallback 06:45 (dateTime: '{DateTime}')", dateTime);
                }
            }
            else
            {
                time = TimeOnly.FromDateTime(parsedDateTime);
                _logger.LogInformation("Using parsed time {Time} from dateTime: '{DateTime}'", time, dateTime);
            }

            // Create template reminder with magic date 1900-01-01
            var templateDate = new DateOnly(1900, 1, 1);

            var templateReminderId = await _reminderRepository.AddReminderAsync(description, templateDate, time, childName);

            // Generate cron expression based on recurrence
            var cronExpression = GenerateCronExpression(recurrenceType, dayOfWeek, time);

            // Create scheduled task for the recurring reminder
            var taskName = $"recurring_reminder_{templateReminderId}";
            var taskDescription = $"Recurring reminder: {description} for {childName ?? "any child"}";

            var scheduledTask = new ScheduledTask
            {
                Name = taskName,
                Description = taskDescription,
                CronExpression = cronExpression,
                TaskType = "reminder",
                ReminderId = templateReminderId,
                Enabled = true
            };

            await _scheduledTaskRepository.AddScheduledTaskAsync(scheduledTask);

            var recurrenceDesc = recurrenceType == "weekly" ? GetDayName(dayOfWeek) : recurrenceType;
            var childInfo = string.IsNullOrEmpty(childName) ? "" : $" for {childName}";

            _logger.LogInformation("Created recurring reminder: {Description} every {Recurrence} at {Time}{ChildInfo}",
                description, recurrenceDesc, time, childInfo);

            return $"✅ Recurring reminder created: '{description}' every {recurrenceDesc} at {time:HH:mm}{childInfo}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create recurring reminder");
            return "❌ Failed to create recurring reminder. Please try again.";
        }
    }

    private string GenerateCronExpression(string recurrenceType, int dayOfWeek, TimeOnly time)
    {
        return recurrenceType switch
        {
            "daily" => $"{time.Minute} {time.Hour} * * *",
            "weekly" => $"{time.Minute} {time.Hour} * * {dayOfWeek}",
            _ => throw new ArgumentException($"Unsupported recurrence type: {recurrenceType}")
        };
    }

    private string GetDayName(int dayOfWeek)
    {
        return dayOfWeek switch
        {
            0 => "Sunday",
            1 => "Monday",
            2 => "Tuesday",
            3 => "Wednesday",
            4 => "Thursday",
            5 => "Friday",
            6 => "Saturday",
            _ => "Unknown"
        };
    }

    public async Task<string> ListRemindersAsync(string? childName = null)
    {
        try
        {
            var reminders = await _reminderRepository.GetAllRemindersAsync();

            if (!string.IsNullOrEmpty(childName))
            {
                reminders = reminders.Where(r => string.Equals(r.ChildName, childName, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (reminders.Count == 0)
            {
                var filterInfo = string.IsNullOrEmpty(childName) ? "" : $" for {childName}";
                return $"No active reminders found{filterInfo}.";
            }

            var reminderList = reminders
                .OrderBy(r => r.RemindDate).ThenBy(r => r.RemindTime)
                .Select((r, index) =>
                {
                    var dateTime = r.RemindDate.ToDateTime(r.RemindTime);
                    return $"{index + 1}. {r.Text} - {dateTime:yyyy-MM-dd HH:mm}" +
                           (string.IsNullOrEmpty(r.ChildName) ? "" : $" ({r.ChildName})");
                })
                .ToList();

            return "Active reminders:\n" + string.Join("\n", reminderList);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list reminders");
            return "❌ Failed to retrieve reminders. Please try again.";
        }
    }

    public async Task<string> DeleteReminderAsync(int reminderNumber)
    {
        try
        {
            var reminders = await _reminderRepository.GetAllRemindersAsync();

            if (reminderNumber < 1 || reminderNumber > reminders.Count)
            {
                return $"❌ Invalid reminder number. Please use a number between 1 and {reminders.Count}.";
            }

            var reminderToDelete = reminders.OrderBy(r => r.RemindDate).ThenBy(r => r.RemindTime).ElementAt(reminderNumber - 1);
            await _reminderRepository.DeleteReminderAsync(reminderToDelete.Id);

            _logger.LogInformation("Deleted reminder: {Text}", reminderToDelete.Text);
            return $"✅ Deleted reminder: '{reminderToDelete.Text}'";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete reminder");
            return "❌ Failed to delete reminder. Please try again.";
        }
    }

    public string GetWeekLetters(string? childName = null)
    {
        try
        {
            var allChildren = _config.MinUddannelse.Children;
            var children = string.IsNullOrEmpty(childName)
                ? allChildren.ToList()
                : allChildren.Where(c => $"{c.FirstName} {c.LastName}".Contains(childName, StringComparison.OrdinalIgnoreCase)).ToList();

            if (children.Count == 0)
            {
                return $"❌ No children found matching '{childName}'.";
            }

            var now = DateTime.Now;
            var weekNumber = System.Globalization.ISOWeek.GetWeekOfYear(now);
            var year = now.Year;

            var weekLetterSummaries = new List<string>();
            foreach (var child in children)
            {
                var weekLetter = _dataService.GetWeekLetter(child, weekNumber, year);
                if (weekLetter != null)
                {
                    var summary = ExtractSummaryFromWeekLetter(weekLetter);
                    weekLetterSummaries.Add($"**{child.FirstName} {child.LastName}** - Week Letter:\n{summary}");
                }
                else
                {
                    weekLetterSummaries.Add($"**{child.FirstName} {child.LastName}** - No week letter available");
                }
            }

            return string.Join("\n\n", weekLetterSummaries);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get week letters");
            return "❌ Failed to retrieve week letters. Please try again.";
        }
    }

    public string GetChildActivities(string childName, string? date = null)
    {
        try
        {
            var targetDate = DateTime.Today;
            if (!string.IsNullOrEmpty(date) && !DateTime.TryParse(date, out targetDate))
            {
                return $"❌ Invalid date format: '{date}'. Please use a valid date format.";
            }
            var child = _config.MinUddannelse.Children.FirstOrDefault(c =>
                $"{c.FirstName} {c.LastName}".Contains(childName, StringComparison.OrdinalIgnoreCase));

            if (child == null)
            {
                return $"❌ Child '{childName}' not found.";
            }

            var weekNumber = System.Globalization.ISOWeek.GetWeekOfYear(targetDate);
            var year = targetDate.Year;

            var weekLetter = _dataService.GetWeekLetter(child, weekNumber, year);
            if (weekLetter == null)
            {
                return $"No week letter available for {child.FirstName} {child.LastName}.";
            }

            var dayOfWeek = targetDate.DayOfWeek;
            var dayName = dayOfWeek.ToString();

            var content = ExtractContentFromWeekLetter(weekLetter);
            var lines = content.Split('\n')
                .Where(line => line.Contains(dayName, StringComparison.OrdinalIgnoreCase) ||
                              line.Contains(targetDate.ToString("dd/MM"), StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (lines.Count > 0)
            {
                return $"**{child.FirstName} {child.LastName}** activities for {targetDate:yyyy-MM-dd} ({dayName}):\n" +
                       string.Join("\n", lines.Select(l => $"• {l.Trim()}"));
            }

            return $"No specific activities found for {child.FirstName} {child.LastName} on {targetDate:yyyy-MM-dd} ({dayName}).";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get child activities");
            return "❌ Failed to retrieve activities. Please try again.";
        }
    }

    public string GetCurrentDateTime()
    {
        var now = DateTime.Now;
        return $"Today is {now:dddd, yyyy-MM-dd} and the current time is {now:HH:mm}.";
    }

    public string GetHelp()
    {
        return @"**Available Commands:**

**Reminder Management:**
• Create reminders for specific dates and times
• List all your active reminders
• Delete reminders by number

**Information Retrieval:**
• Get week letters for your children
• Get specific child activities by date
• Get current date and time

**Natural Language Examples:**
• ""Remind me tomorrow at 8 AM that Søren Johannes has soccer practice""
• ""What activities does Emma have on Friday?""
• ""Show me this week's letter for all children""
• ""List my reminders and delete the second one""

Just ask me naturally and I'll help you!";
    }

    private string ExtractSummaryFromWeekLetter(Newtonsoft.Json.Linq.JObject weekLetter)
    {
        if (weekLetter["summary"] != null)
        {
            return weekLetter["summary"]?.ToString() ?? "";
        }

        var content = ExtractContentFromWeekLetter(weekLetter);
        return content.Length > 300 ? content.Substring(0, 300) + "..." : content;
    }

    private string ExtractContentFromWeekLetter(Newtonsoft.Json.Linq.JObject weekLetter)
    {
        if (weekLetter["content"] != null)
        {
            return weekLetter["content"]?.ToString() ?? "";
        }

        if (weekLetter["text"] != null)
        {
            return weekLetter["text"]?.ToString() ?? "";
        }

        return weekLetter.ToString();
    }
}
