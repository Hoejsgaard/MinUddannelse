using Microsoft.Extensions.Logging;
using MinUddannelse.AI.Prompts;
using MinUddannelse.Configuration;
using MinUddannelse.Repositories;
using MinUddannelse.Models;
using MinUddannelse.Repositories.DTOs;
using Newtonsoft.Json.Linq;
using OpenAI;
using OpenAI.ObjectModels;
using OpenAI.ObjectModels.RequestModels;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MinUddannelse.AI.Services;

public class WeekLetterReminderService : IWeekLetterReminderService
{
    private readonly IOpenAiService _openAiClient;
    private readonly ILogger<WeekLetterReminderService> _logger;
    private readonly IWeekLetterRepository _weekLetterRepository;
    private readonly IReminderRepository _reminderRepository;
    private readonly string _aiModel;
    private readonly TimeOnly _defaultReminderTime;

    public WeekLetterReminderService(
        IOpenAiService openAiService,
        ILogger<WeekLetterReminderService> logger,
        IWeekLetterRepository weekLetterRepository,
        IReminderRepository reminderRepository,
        string aiModel,
        TimeOnly defaultReminderTime)
    {
        ArgumentNullException.ThrowIfNull(openAiService);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(weekLetterRepository);
        ArgumentNullException.ThrowIfNull(reminderRepository);
        ArgumentException.ThrowIfNullOrWhiteSpace(aiModel);

        _openAiClient = openAiService;
        _logger = logger;
        _weekLetterRepository = weekLetterRepository;
        _reminderRepository = reminderRepository;
        _aiModel = aiModel;
        _defaultReminderTime = defaultReminderTime;
    }

    public async Task<ReminderExtractionResult> ExtractAndStoreRemindersAsync(
        string childName,
        int weekNumber,
        int year,
        JObject weekLetter,
        string contentHash)
    {
        try
        {
            _logger.LogInformation("Starting reminder extraction for {ChildName} week {WeekNumber}/{Year}",
                childName, weekNumber, year);

            var postedLetter = await _weekLetterRepository.GetPostedLetterByHashAsync(childName, weekNumber, year);
            if (postedLetter == null)
            {
                _logger.LogWarning("Cannot extract reminders - no posted letter found for {ChildName} week {WeekNumber}/{Year}",
                    childName, weekNumber, year);
                return new ReminderExtractionResult { Success = false, RemindersCreated = 0, ErrorMessage = "No posted letter found" };
            }

            if (postedLetter.AutoRemindersExtracted == true)
            {
                _logger.LogInformation("Reminders already extracted for {ChildName} week {WeekNumber}/{Year}, skipping",
                    childName, weekNumber, year);
                return new ReminderExtractionResult { Success = true, RemindersCreated = 0, AlreadyProcessed = true };
            }

            var weekLetterContent = ExtractContentFromWeekLetter(weekLetter);
            if (string.IsNullOrWhiteSpace(weekLetterContent))
            {
                _logger.LogWarning("No content found in week letter for {ChildName} week {WeekNumber}/{Year}",
                    childName, weekNumber, year);
                return new ReminderExtractionResult { Success = true, RemindersCreated = 0, NoRemindersFound = true };
            }

            var extractedEvents = await ExtractEventsFromWeekLetterAsync(weekLetterContent);

            if (!extractedEvents.Any())
            {
                _logger.LogInformation("No actionable events found in week letter for {ChildName} week {WeekNumber}/{Year}",
                    childName, weekNumber, year);

                await _weekLetterRepository.MarkAutoRemindersExtractedAsync(childName, weekNumber, year);

                return new ReminderExtractionResult { Success = true, RemindersCreated = 0, NoRemindersFound = true };
            }

            var weekLetterId = postedLetter?.Id;
            if (weekLetterId.HasValue)
            {
                await _reminderRepository.DeleteAutoExtractedRemindersByWeekLetterIdAsync(weekLetterId.Value);
            }

            var remindersCreated = 0;
            var createdReminderInfos = new List<CreatedReminderInfo>();

            foreach (var extractedEvent in extractedEvents)
            {
                try
                {
                    var reminderText = extractedEvent.Description;
                    await _reminderRepository.AddAutoReminderAsync(
                        reminderText,
                        DateOnly.FromDateTime(extractedEvent.EventDate),
                        _defaultReminderTime,
                        childName,
                        weekLetterId ?? 0,
                        extractedEvent.EventType,
                        extractedEvent.Title,
                        (decimal)extractedEvent.ConfidenceScore);
                    remindersCreated++;

                    createdReminderInfos.Add(new CreatedReminderInfo
                    {
                        Title = reminderText,
                        Date = extractedEvent.EventDate,
                        EventTime = null,
                        IsCurrentWeek = extractedEvent.IsCurrentWeek,
                        DayOfWeek = extractedEvent.DayOfWeek
                    });

                    _logger.LogInformation("Created auto-extracted reminder for {ChildName}: {ReminderText}",
                        childName, reminderText);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create reminder for extracted event: {EventTitle}",
                        extractedEvent.Title);
                }
            }

            await _weekLetterRepository.MarkAutoRemindersExtractedAsync(childName, weekNumber, year);

            _logger.LogInformation("Successfully created {Count} reminders for {ChildName} week {WeekNumber}/{Year}",
                remindersCreated, childName, weekNumber, year);

            return new ReminderExtractionResult
            {
                Success = true,
                RemindersCreated = remindersCreated,
                CreatedReminders = createdReminderInfos
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract reminders for {ChildName} week {WeekNumber}/{Year}",
                childName, weekNumber, year);
            return new ReminderExtractionResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private string ExtractContentFromWeekLetter(JObject weekLetter)
    {
        try
        {
            var contentFields = new[] { "content", "Content", "text", "Text", "body", "Body", "html", "Html" };

            foreach (var field in contentFields)
            {
                var fieldValue = weekLetter[field]?.ToString();
                if (!string.IsNullOrWhiteSpace(fieldValue))
                {
                    var cleaned = System.Text.RegularExpressions.Regex.Replace(fieldValue, @"<[^>]+>", " ");
                    cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ");

                    cleaned = System.Net.WebUtility.HtmlDecode(cleaned);

                    return cleaned.Trim();
                }
            }

            return weekLetter.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<List<ExtractedEvent>> ExtractEventsFromWeekLetterAsync(string weekLetterContent)
    {
        try
        {
            var prompt = ReminderExtractionPrompts.GetWeekLetterEventExtractionPrompt(weekLetterContent, DateTime.Now);

            var completionResult = await _openAiClient.CreateCompletionAsync(prompt, _aiModel);

            if (!completionResult.IsSuccess || string.IsNullOrWhiteSpace(completionResult.Content))
            {
                _logger.LogWarning("OpenAI completion failed or returned empty content");
                return new List<ExtractedEvent>();
            }

            return ParseExtractedEvents(completionResult.Content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract events from week letter using AI");
            return new List<ExtractedEvent>();
        }
    }

    private List<ExtractedEvent> ParseExtractedEvents(string aiResponse)
    {
        try
        {
            var cleanedResponse = aiResponse.Trim();
            if (cleanedResponse.StartsWith("```json"))
            {
                cleanedResponse = cleanedResponse.Substring(7);
            }
            if (cleanedResponse.EndsWith("```"))
            {
                cleanedResponse = cleanedResponse.Substring(0, cleanedResponse.Length - 3);
            }
            cleanedResponse = cleanedResponse.Trim();

            var jsonResponse = JObject.Parse(cleanedResponse);
            var extractedEvents = new List<ExtractedEvent>();

            // Parse current week events
            var thisWeekEvents = jsonResponse["this_week"]?.ToArray() ?? Array.Empty<JToken>();
            foreach (var weekEvent in thisWeekEvents)
            {
                var extractedEvent = new ExtractedEvent
                {
                    Title = System.Net.WebUtility.HtmlDecode(weekEvent["title"]?.ToString() ?? string.Empty),
                    Description = System.Net.WebUtility.HtmlDecode(weekEvent["description"]?.ToString() ?? string.Empty),
                    EventDate = DateTime.Parse(weekEvent["date"]?.ToString() ?? DateTime.Today.ToString("yyyy-MM-dd")),
                    EventType = weekEvent["type"]?.ToString() ?? "event",
                    ConfidenceScore = weekEvent["confidence"]?.Value<double>() ?? 0.8,
                    IsCurrentWeek = true,
                    DayOfWeek = weekEvent["day"]?.ToString()
                };

                if (extractedEvent.ConfidenceScore >= 0.95)
                {
                    extractedEvents.Add(extractedEvent);
                }
            }

            // Parse future events
            var futureEvents = jsonResponse["future"]?.ToArray() ?? Array.Empty<JToken>();
            foreach (var futureEvent in futureEvents)
            {
                var extractedEvent = new ExtractedEvent
                {
                    Title = System.Net.WebUtility.HtmlDecode(futureEvent["title"]?.ToString() ?? string.Empty),
                    Description = System.Net.WebUtility.HtmlDecode(futureEvent["description"]?.ToString() ?? string.Empty),
                    EventDate = DateTime.Parse(futureEvent["date"]?.ToString() ?? DateTime.Today.ToString("yyyy-MM-dd")),
                    EventType = futureEvent["type"]?.ToString() ?? "event",
                    ConfidenceScore = futureEvent["confidence"]?.Value<double>() ?? 0.8,
                    IsCurrentWeek = false
                };

                if (extractedEvent.ConfidenceScore >= 0.95)
                {
                    extractedEvents.Add(extractedEvent);
                }
            }

            _logger.LogInformation("Parsed {ThisWeekCount} current week and {FutureCount} future events",
                thisWeekEvents.Length, futureEvents.Length);

            return extractedEvents;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse AI response as JSON: {Response}", aiResponse);
            return new List<ExtractedEvent>();
        }
    }




    private string? ExtractEventTimeFromContent(string eventTitle, string weekLetterContent)
    {
        try
        {
            var titleIndex = weekLetterContent.IndexOf(eventTitle, StringComparison.OrdinalIgnoreCase);
            if (titleIndex == -1) return null;

            var start = Math.Max(0, titleIndex - 100);
            var end = Math.Min(weekLetterContent.Length, titleIndex + eventTitle.Length + 100);
            var contextWindow = weekLetterContent.Substring(start, end - start);

            var timePattern = @"(?:kl\.?\s*)?(\d{1,2}[:.]\d{2}(?:-\d{1,2}[:.]\d{2})?)";
            var timeMatch = Regex.Match(contextWindow, timePattern, RegexOptions.IgnoreCase);

            if (timeMatch.Success)
            {
                return timeMatch.Groups[1].Value.Replace(":", ".");
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private string ImproveReminderTitle(string originalTitle)
    {
        var improved = originalTitle.Trim();

        var prefixesToRemove = new[] { "Event: ", "Deadline: ", "Activity: ", "Task: " };
        foreach (var prefix in prefixesToRemove)
        {
            if (improved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                improved = improved.Substring(prefix.Length).Trim();
            }
        }

        if (improved.Length > 0)
        {
            improved = char.ToUpper(improved[0]) + improved.Substring(1);
        }

        return improved;
    }
}

public class ExtractedEvent
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime EventDate { get; set; }
    public string EventType { get; set; } = "event";
    public double ConfidenceScore { get; set; } = 0.8;
    public bool IsCurrentWeek { get; set; } = false;
    public string? DayOfWeek { get; set; }
}
