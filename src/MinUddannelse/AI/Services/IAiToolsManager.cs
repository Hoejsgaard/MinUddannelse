namespace MinUddannelse.AI.Services;
using MinUddannelse.Models;
using MinUddannelse.Repositories.DTOs;

public interface IAiToolsManager
{
    Task<string> CreateReminderAsync(string description, string dateTime, string? childName = null);
    Task<string> CreateRecurringReminderAsync(string description, string dateTime, string recurrenceType, int dayOfWeek, string? childName = null);
    Task<string> ListRemindersAsync(string? childName = null);
    Task<string> DeleteReminderAsync(int reminderNumber);
    string GetWeekLetters(string? childName = null);
    string GetChildActivities(string childName, string? date = null);
    string GetCurrentDateTime();
    string GetHelp();
}
