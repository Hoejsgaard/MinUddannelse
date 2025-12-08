using MinUddannelse.Models;
using MinUddannelse.Repositories.DTOs;
using Microsoft.Extensions.Logging;
using Supabase;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MinUddannelse.Repositories;

public class ReminderRepository : IReminderRepository
{
    private readonly Supabase.Client _supabase;
    private readonly ILogger _logger;

    public ReminderRepository(Supabase.Client supabase, ILoggerFactory loggerFactory)
    {
        _supabase = supabase ?? throw new ArgumentNullException(nameof(supabase));
        _logger = loggerFactory.CreateLogger<ReminderRepository>();
    }

    public async Task<int> AddReminderAsync(string text, DateOnly date, TimeOnly time, string? childName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (!string.IsNullOrEmpty(childName))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(childName);
        }

        var reminder = new Reminder
        {
            Text = text,
            RemindDate = date,
            RemindTime = time,
            ChildName = childName,
            CreatedBy = "bot"
        };

        var insertResponse = await _supabase
            .From<Reminder>()
            .Insert(reminder);

        var insertedReminder = insertResponse.Models.FirstOrDefault();
        if (insertedReminder == null)
        {
            throw new InvalidOperationException("Failed to insert reminder");
        }

        _logger.LogInformation("Added reminder with ID {ReminderId}: {Text}", insertedReminder.Id, text);
        return insertedReminder.Id;
    }

    public async Task<List<Reminder>> GetPendingRemindersAsync()
    {
        // Reminder times are stored in local Copenhagen time, so use local time for comparison
        var now = DateTime.Now;
        var currentDate = DateOnly.FromDateTime(now);
        var currentTime = TimeOnly.FromDateTime(now);

        // Get all unsent reminders and filter in memory to avoid DateOnly serialization issues with PostgreSQL
        var reminders = await _supabase
            .From<Reminder>()
            .Select("*")
            .Where(r => r.IsSent == false)
            .Get();

        var pendingReminders = reminders.Models
            .Where(r => r.RemindDate < currentDate || (r.RemindDate == currentDate && r.RemindTime <= currentTime))
            .Where(r => r.RemindDate != new DateOnly(1900, 1, 1)) // Exclude template reminders
            .OrderBy(r => r.RemindDate)
            .ThenBy(r => r.RemindTime)
            .ToList();

        _logger.LogInformation("Found {Count} pending reminders", pendingReminders.Count);
        return pendingReminders;
    }

    public async Task MarkReminderAsSentAsync(int reminderId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(reminderId, 0);

        await _supabase
            .From<Reminder>()
            .Where(r => r.Id == reminderId)
            .Set(r => r.IsSent, true)
            .Update();

        _logger.LogInformation("Marked reminder {ReminderId} as sent", reminderId);
    }

    public async Task MarkReminderAsUnsentAsync(int reminderId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(reminderId, 0);

        await _supabase
            .From<Reminder>()
            .Where(r => r.Id == reminderId)
            .Set(r => r.IsSent, false)
            .Update();

        _logger.LogInformation("Marked reminder {ReminderId} as unsent for retry", reminderId);
    }

    public async Task<List<Reminder>> GetAllRemindersAsync()
    {
        var reminders = await _supabase
            .From<Reminder>()
            .Select("*")
            .Order("remind_date", Supabase.Postgrest.Constants.Ordering.Descending)
            .Order("remind_time", Supabase.Postgrest.Constants.Ordering.Descending)
            .Get();

        return reminders.Models;
    }

    public async Task<Reminder?> GetReminderByIdAsync(int reminderId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(reminderId, 0);

        var result = await _supabase
            .From<Reminder>()
            .Select("*")
            .Where(r => r.Id == reminderId)
            .Get();

        var reminder = result.Models.FirstOrDefault();

        if (reminder != null)
        {
            _logger.LogInformation("Retrieved reminder {ReminderId}: {Text}", reminderId, reminder.Text);
        }
        else
        {
            _logger.LogWarning("Reminder {ReminderId} not found", reminderId);
        }

        return reminder;
    }

    public async Task DeleteReminderAsync(int reminderId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(reminderId, 0);

        await _supabase
            .From<Reminder>()
            .Where(r => r.Id == reminderId)
            .Delete();

        _logger.LogInformation("Deleted reminder with ID {ReminderId}", reminderId);
    }

    public async Task<int> AddAutoReminderAsync(string text, DateOnly date, TimeOnly time, string childName, int weekLetterId, string eventType, string eventTitle, decimal confidenceScore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(childName);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventTitle);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(weekLetterId, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(confidenceScore, 0.1m);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(confidenceScore, 1.0m);

        var reminder = new Reminder
        {
            Text = text,
            RemindDate = date,
            RemindTime = time,
            CreatedAt = DateTime.UtcNow,
            IsSent = false,
            ChildName = childName,
            CreatedBy = "auto_extraction",
            Source = "auto_extracted",
            WeekLetterId = weekLetterId,
            EventType = eventType,
            EventTitle = eventTitle,
            ExtractedDateTime = DateTime.UtcNow,
            ConfidenceScore = confidenceScore
        };

        var result = await _supabase
            .From<Reminder>()
            .Insert(reminder);

        var insertedReminder = result.Models.First();

        _logger.LogInformation("Added auto-extracted reminder {ReminderId} for {ChildName}: {Text}",
            insertedReminder.Id, childName, text);

        return insertedReminder.Id;
    }

    public async Task DeleteAutoExtractedRemindersByWeekLetterIdAsync(int weekLetterId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(weekLetterId, 0);

        await _supabase
            .From<Reminder>()
            .Where(r => r.WeekLetterId == weekLetterId && r.Source == "auto_extracted")
            .Delete();

        _logger.LogInformation("Deleted auto-extracted reminders for week letter {WeekLetterId}", weekLetterId);
    }

    public async Task<bool> ReminderExistsForDateAsync(string text, DateOnly date, TimeOnly time, string? childName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        // Filter by text in the query to reduce data transfer
        // DateOnly/TimeOnly and nullable childName comparisons are done in memory
        var reminders = await _supabase
            .From<Reminder>()
            .Select("*")
            .Where(r => r.Text == text)
            .Get();

        var exists = reminders.Models.Any(r =>
            r.RemindDate == date &&
            r.RemindTime == time &&
            r.ChildName == childName);

        if (exists)
        {
            _logger.LogInformation("Reminder already exists for {Date} {Time}: {Text} ({ChildName})",
                date, time, text, childName ?? "any");
        }

        return exists;
    }
}
