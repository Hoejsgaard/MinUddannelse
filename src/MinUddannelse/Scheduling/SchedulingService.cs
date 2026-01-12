using Microsoft.Extensions.Logging;
using MinUddannelse.Content.Processing;
using MinUddannelse.Content.WeekLetters;
using MinUddannelse.Models;
using MinUddannelse.Repositories.DTOs;
using NCrontab;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MinUddannelse.Client;
using MinUddannelse.Configuration;
using MinUddannelse.AI.Services;
using MinUddannelse.Repositories;
using MinUddannelse.Events;

namespace MinUddannelse.Scheduling;

public class SchedulingService : ISchedulingService
{
    private readonly ILogger _logger;
    private readonly IReminderRepository _reminderRepository;
    private readonly IScheduledTaskRepository _scheduledTaskRepository;
    private readonly IWeekLetterRepository _weekLetterRepository;
    private readonly IRetryTrackingRepository _retryTrackingRepository;
    private readonly IAppStateRepository _appStateRepository;
    private readonly IWeekLetterService _weekLetterService;
    private readonly IWeekLetterReminderService _weekLetterReminderService;
    private readonly Config _config;
    private Timer? _schedulingTimer;
    private readonly object _lockObject = new object();
    private bool _isRunning;

    private const int SchedulingWindowSeconds = 10;

    public event EventHandler<ChildWeekLetterEventArgs>? ChildWeekLetterReady;
    public event EventHandler<ChildReminderEventArgs>? ReminderReady;
    public event EventHandler<ChildMessageEventArgs>? MessageReady;

    public void TriggerChildWeekLetterReady(ChildWeekLetterEventArgs args)
    {
        ChildWeekLetterReady?.Invoke(this, args);
    }

    public SchedulingService(
        ILoggerFactory loggerFactory,
        IReminderRepository reminderRepository,
        IScheduledTaskRepository scheduledTaskRepository,
        IWeekLetterRepository weekLetterRepository,
        IRetryTrackingRepository retryTrackingRepository,
        IAppStateRepository appStateRepository,
        IWeekLetterService weekLetterService,
        IWeekLetterReminderService weekLetterReminderService,
        Config config)
    {
        _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger<SchedulingService>();
        _reminderRepository = reminderRepository ?? throw new ArgumentNullException(nameof(reminderRepository));
        _scheduledTaskRepository = scheduledTaskRepository ?? throw new ArgumentNullException(nameof(scheduledTaskRepository));
        _weekLetterRepository = weekLetterRepository ?? throw new ArgumentNullException(nameof(weekLetterRepository));
        _retryTrackingRepository = retryTrackingRepository ?? throw new ArgumentNullException(nameof(retryTrackingRepository));
        _appStateRepository = appStateRepository ?? throw new ArgumentNullException(nameof(appStateRepository));
        _weekLetterService = weekLetterService ?? throw new ArgumentNullException(nameof(weekLetterService));
        _weekLetterReminderService = weekLetterReminderService ?? throw new ArgumentNullException(nameof(weekLetterReminderService));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public Task StartAsync()
    {
        _logger.LogInformation("Starting scheduling service");

        lock (_lockObject)
        {
            if (_isRunning)
            {
                _logger.LogWarning("Scheduling service is already running");
                return Task.CompletedTask;
            }

            _isRunning = true;
        }

        var timerInterval = TimeSpan.FromSeconds(_config.Scheduling.IntervalSeconds);
        _schedulingTimer = new Timer(CheckScheduledTasksWrapper, null, TimeSpan.Zero, timerInterval);
        _logger.LogInformation("Scheduling service timer started - checking every {IntervalSeconds} seconds", _config.Scheduling.IntervalSeconds);

        _ = Task.Run(async () =>
        {
            try
            {
                await CheckForMissedReminders();
                await CheckForMissedScheduledTasks();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking for missed reminders/tasks on startup");
            }
        });

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _logger.LogInformation("Stopping scheduling service");

        lock (_lockObject)
        {
            _isRunning = false;
        }

        try
        {
            _schedulingTimer?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        _logger.LogInformation("Scheduling service stopped");

        return Task.CompletedTask;
    }

    private void CheckScheduledTasksWrapper(object? state)
    {
        _logger.LogInformation("TIMER FIRED - CheckScheduledTasksWrapper called at {UtcTime} UTC", DateTime.UtcNow);

        _ = Task.Run(async () =>
        {
            try
            {
                await CheckScheduledTasks();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in scheduled task check");
            }
        });
    }

    private async Task CheckScheduledTasks()
    {
        if (!_isRunning) return;

        try
        {
            _logger.LogInformation("Timer fired: Checking scheduled tasks and reminders at {UtcTime} UTC", DateTime.UtcNow);

            await ExecutePendingReminders();
            await ExecutePendingRetries();

            var currentSecond = DateTime.UtcNow.Second;
            if (currentSecond < SchedulingWindowSeconds)
            {
                _logger.LogInformation("Running scheduled tasks check at {UtcTime} UTC", DateTime.UtcNow);

                var tasks = await _scheduledTaskRepository.GetScheduledTasksAsync();
                var now = DateTime.UtcNow;

                foreach (var task in tasks)
                {
                    try
                    {
                        if (ShouldRunTask(task, now))
                        {
                            _logger.LogInformation("Executing scheduled task: {TaskName}", task.Name);

                            task.LastRun = now;
                            task.NextRun = GetNextRunTime(task.CronExpression, now.AddMinutes(2));
                            await _scheduledTaskRepository.UpdateScheduledTaskAsync(task);
                            await ExecuteTask(task);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing scheduled task: {TaskName}", task.Name);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CRITICAL: Error in CheckScheduledTasks - this could cause app crashes");
        }
    }

    private bool ShouldRunTask(ScheduledTask task, DateTime utcNow)
    {
        _logger.LogInformation("ShouldRunTask check for {TaskName}: Enabled={Enabled}, LastRun={LastRun}, NextRun={NextRun}, UtcNow={UtcNow}",
            task.Name, task.Enabled, task.LastRun, task.NextRun, utcNow);

        if (!task.Enabled)
        {
            _logger.LogInformation("Task {TaskName} is disabled", task.Name);
            return false;
        }

        try
        {
            DateTime nextRun;

            if (task.NextRun.HasValue)
            {
                nextRun = task.NextRun.Value;
                _logger.LogInformation("Task {TaskName} using database NextRun: {NextRun}", task.Name, nextRun);
            }
            else
            {
                var schedule = CrontabSchedule.Parse(task.CronExpression);
                nextRun = task.LastRun != null
                    ? schedule.GetNextOccurrence(task.LastRun.Value)
                    : schedule.GetNextOccurrence(utcNow.AddMinutes(-_config.Scheduling.InitialOccurrenceOffsetMinutes));
                _logger.LogInformation("Task {TaskName} calculated NextRun from cron: {NextRun}", task.Name, nextRun);
            }

            _logger.LogInformation("Task {TaskName} cron analysis: CronExpression={CronExpression}, FinalNextRun={FinalNextRun}, UtcNow={UtcNow}, WindowMinutes={WindowMinutes}",
                task.Name, task.CronExpression, nextRun, utcNow, _config.Scheduling.TaskExecutionWindowMinutes);

            var shouldRun = utcNow >= nextRun && utcNow <= nextRun.AddMinutes(_config.Scheduling.TaskExecutionWindowMinutes);
            _logger.LogInformation("Task {TaskName} should run: {ShouldRun} (utcNow >= nextRun: {IsAfterNext}, utcNow <= window: {IsInWindow})",
                task.Name, shouldRun, utcNow >= nextRun, utcNow <= nextRun.AddMinutes(_config.Scheduling.TaskExecutionWindowMinutes));

            return shouldRun;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Invalid cron expression for task {TaskName}: {CronExpression}",
                task.Name, task.CronExpression);
            return false;
        }
    }

    private DateTime? GetNextRunTime(string cronExpression, DateTime fromTime)
    {
        try
        {
            var schedule = CrontabSchedule.Parse(cronExpression);
            return schedule.GetNextOccurrence(fromTime);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating next run time for cron: {CronExpression}", cronExpression);
            return null;
        }
    }

    private async Task ExecuteTask(ScheduledTask task)
    {
        // Handle tasks by their TaskType first, then fall back to Name for legacy tasks
        switch (task.TaskType)
        {
            case "reminder":
                await ExecuteRecurringReminder(task);
                break;

            case "hardcoded":
            default:
                // Handle legacy hardcoded tasks by their Name
                switch (task.Name)
                {
                    case "ReminderCheck":
                        await ExecutePendingReminders();
                        break;

                    case "WeeklyLetterCheck":
                        await ExecuteWeeklyLetterCheck(task);
                        break;

                    default:
                        _logger.LogWarning("Unknown scheduled task: {TaskName} with TaskType: {TaskType}", task.Name, task.TaskType);
                        break;
                }
                break;
        }
    }

    private async Task ExecuteRecurringReminder(ScheduledTask task)
    {
        try
        {
            if (!task.ReminderId.HasValue)
            {
                _logger.LogError("Recurring reminder task {TaskName} has no ReminderId", task.Name);
                return;
            }

            // Load the template reminder
            var templateReminder = await _reminderRepository.GetReminderByIdAsync(task.ReminderId.Value);
            if (templateReminder == null)
            {
                _logger.LogError("Template reminder with ID {ReminderId} not found for task {TaskName}",
                    task.ReminderId.Value, task.Name);
                return;
            }

            // Use local time for reminder date since reminders are displayed in local time
            var today = DateOnly.FromDateTime(DateTime.Now);
            var reminderTime = templateReminder.RemindTime;

            // Check if reminder already exists for today to prevent duplicates
            if (await _reminderRepository.ReminderExistsForDateAsync(
                templateReminder.Text, today, reminderTime, templateReminder.ChildName))
            {
                _logger.LogInformation("Recurring reminder already exists for today, skipping: '{Description}' for {ChildName}",
                    templateReminder.Text, templateReminder.ChildName);
                return;
            }

            var newReminderId = await _reminderRepository.AddReminderAsync(
                templateReminder.Text,
                today,
                reminderTime,
                templateReminder.ChildName);

            _logger.LogInformation("Created recurring reminder {NewReminderId} from template {TemplateId}: '{Description}' for {ChildName} at {Time}",
                newReminderId, templateReminder.Id, templateReminder.Text, templateReminder.ChildName, reminderTime);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing recurring reminder task {TaskName}", task.Name);
        }
    }

    private async Task ExecutePendingReminders()
    {
        try
        {
            _logger.LogInformation("ExecutePendingReminders called at {UtcTime} UTC", DateTime.UtcNow);

            var pendingReminders = await _reminderRepository.GetPendingRemindersAsync();

            if (pendingReminders.Count == 0)
            {
                _logger.LogInformation("No pending reminders found");
                return;
            }

            _logger.LogInformation("Found {Count} pending reminders to send", pendingReminders.Count);

            foreach (var reminder in pendingReminders)
            {
                try
                {
                    // Mark as sent BEFORE sending (optimistic locking) to prevent race conditions
                    // This prevents multiple scheduler cycles from picking up the same reminder
                    await _reminderRepository.MarkReminderAsSentAsync(reminder.Id);

                    SendReminderNotification(reminder);

                    _logger.LogInformation("Sent reminder {ReminderId}: {Text}", reminder.Id, reminder.Text);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error sending reminder {ReminderId} - marking as unsent for retry", reminder.Id);

                    // If sending failed, mark it back as unsent so it can be retried later
                    try
                    {
                        await _reminderRepository.MarkReminderAsUnsentAsync(reminder.Id);
                    }
                    catch (Exception markEx)
                    {
                        _logger.LogError(markEx, "Failed to mark reminder {ReminderId} as unsent after send failure", reminder.Id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing pending reminders");
        }
    }

    private void SendReminderNotification(Reminder reminder)
    {
        if (string.IsNullOrEmpty(reminder.ChildName))
        {
            _logger.LogError("Reminder {ReminderId} has no child name - cannot send (all reminders must be child-specific)", reminder.Id);
            throw new InvalidOperationException($"Reminder {reminder.Id} has no child name and cannot be sent");
        }

        var childId = Configuration.Child.GenerateChildId(reminder.ChildName);
        var eventArgs = new ChildReminderEventArgs(childId, reminder.ChildName, reminder);
        ReminderReady?.Invoke(this, eventArgs);

        _logger.LogInformation("Fired reminder event {ReminderId} for {ChildName}",
            reminder.Id, reminder.ChildName);
    }

    private async Task ExecuteWeeklyLetterCheck(ScheduledTask task)
    {
        try
        {
            _logger.LogInformation("Executing weekly letter check");

            var children = _config.MinUddannelse?.Children ?? new List<Child>();
            if (!children.Any())
            {
                _logger.LogWarning("No children configured for week letter check");
                return;
            }

            foreach (var child in children)
            {
                try
                {
                    await CheckAndPostWeekLetter(child, task);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error checking week letter for child: {ChildName}", child.FirstName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing weekly letter check");
        }
    }

    private async Task CheckAndPostWeekLetter(Child child, ScheduledTask task)
    {
        var (weekNumber, year) = GetCurrentWeekAndYear();

        // Use local time for Sunday check - relates to Danish school calendar context
        if (DateTime.Now.DayOfWeek == DayOfWeek.Sunday)
        {
            var nextWeek = DateTime.Now.AddDays(7);
            weekNumber = System.Globalization.ISOWeek.GetWeekOfYear(nextWeek);
            year = nextWeek.Year;
        }

        try
        {
            _logger.LogInformation("Checking week letter for {ChildName}", child.FirstName);

            if (await IsWeekLetterAlreadyPosted(child.FirstName, weekNumber, year))
                return;

            var weekLetter = await TryGetWeekLetter(child, weekNumber, year);
            if (weekLetter == null)
                return;

            var result = await ValidateAndProcessWeekLetterContent(weekLetter, child.FirstName, weekNumber, year);
            if (result.Item1 == null)
                return;

            if (await IsContentAlreadyPosted(child, result.Item2!, weekNumber, year))
                return;

            var childId = child.GetChildId();
            var eventArgs = new ChildWeekLetterEventArgs(
                childId,
                child.FirstName,
                weekNumber,
                year,
                weekLetter);

            ChildWeekLetterReady?.Invoke(this, eventArgs);
            await _weekLetterRepository.MarkWeekLetterAsPostedAsync(child.FirstName, weekNumber, year, result.Item2!);

            // Add a small delay to ensure week letter is posted before reminders
            await Task.Delay(1000);

            await ExtractRemindersFromWeekLetter(child.FirstName, weekNumber, year, weekLetter, result.Item2!);

            _logger.LogInformation("Emitted week letter event for {ChildName}", child.FirstName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing week letter for {ChildName}", child.FirstName);
            var _ = await _retryTrackingRepository.IncrementRetryAttemptAsync(child.FirstName, weekNumber, year);
        }
    }

    private async Task ExtractRemindersFromWeekLetter(string childName, int weekNumber, int year, JObject weekLetter, string contentHash)
    {
        try
        {
            _logger.LogInformation("Extracting reminders from week letter for {ChildName} week {WeekNumber}/{Year}",
                childName, weekNumber, year);

            var extractionResult = await _weekLetterReminderService.ExtractAndStoreRemindersAsync(
                childName, weekNumber, year, weekLetter, contentHash);

            if (extractionResult.Success && extractionResult.RemindersCreated > 0)
            {
                _logger.LogInformation("Successfully created {Count} reminders for {ChildName}",
                    extractionResult.RemindersCreated, childName);

                var successMessage = FormatReminderSuccessMessage(extractionResult.RemindersCreated, weekNumber, extractionResult.CreatedReminders);
                var childId = Configuration.Child.GenerateChildId(childName);
                var eventArgs = new ChildMessageEventArgs(childId, childName, successMessage, "ai_analysis_success");
                MessageReady?.Invoke(this, eventArgs);
            }
            else if (extractionResult.Success && extractionResult.NoRemindersFound)
            {
                _logger.LogInformation("No reminders found in week letter for {ChildName}", childName);

                var noRemindersMessage = $"Ingen påmindelser blev fundet i ugebrevet for uge {weekNumber}/{year} - der er ikke oprettet nogen automatiske påmindelser for denne uge.";
                var childId = Configuration.Child.GenerateChildId(childName);
                var eventArgs = new ChildMessageEventArgs(childId, childName, noRemindersMessage, "ai_analysis_no_reminders");
                MessageReady?.Invoke(this, eventArgs);
            }
            else if (!extractionResult.Success)
            {
                _logger.LogError("Failed to extract reminders for {ChildName}: {Error}",
                    childName, extractionResult.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during reminder extraction for {ChildName} week {WeekNumber}/{Year}",
                childName, weekNumber, year);
        }
    }

    private static (int weekNumber, int year) GetCurrentWeekAndYear()
    {
        // Use local time for week number calculation as this relates to Danish school calendar
        var localNow = DateTime.Now;
        return (System.Globalization.ISOWeek.GetWeekOfYear(localNow), localNow.Year);
    }

    private async Task<bool> IsWeekLetterAlreadyPosted(string childName, int weekNumber, int year)
    {
        var alreadyPosted = await _weekLetterRepository.HasWeekLetterBeenPostedAsync(childName, weekNumber, year);
        if (alreadyPosted)
        {
            _logger.LogInformation("Week letter for {ChildName} week {WeekNumber}/{Year} already posted",
                childName, weekNumber, year);
        }
        return alreadyPosted;
    }

    private async Task<dynamic?> TryGetWeekLetter(Child child, int weekNumber, int year)
    {
        // Convert week/year back to date for the live fetch method
        var firstDayOfWeek = ISOWeek.ToDateTime(year, weekNumber, DayOfWeek.Monday);
        var date = DateOnly.FromDateTime(firstDayOfWeek);

        var weekLetter = await _weekLetterService.GetOrFetchWeekLetterAsync(child, date, true);
        if (weekLetter == null || WeekLetterService.IsWeekLetterEffectivelyEmpty(weekLetter))
        {
            _logger.LogWarning("No week letter available for {ChildName}, will retry later", child.FirstName);
            var isFirstAttempt = await _retryTrackingRepository.IncrementRetryAttemptAsync(child.FirstName, weekNumber, year);

            if (isFirstAttempt)
            {
                var retryHours = _config.WeekLetter.RetryIntervalHours;
                var maxRetryHours = _config.WeekLetter.MaxRetryDurationHours;
                var totalAttempts = maxRetryHours / retryHours;

                var retryMessage = $"⚠️ Ugebrev for uge {weekNumber}/{year} er endnu ikke tilgængeligt.\n\n" +
                                 $"Jeg vil automatisk prøve igen hver {retryHours}. time i de næste {maxRetryHours} timer " +
                                 $"(op til {totalAttempts} forsøg).\n\n" +
                                 $"Du vil få besked, så snart ugebrevet bliver tilgængeligt! ✅";

                var childId = Configuration.Child.GenerateChildId(child.FirstName);
                var eventArgs = new ChildMessageEventArgs(childId, child.FirstName, retryMessage, "week_letter_retry_started");
                MessageReady?.Invoke(this, eventArgs);

                _logger.LogInformation("Sent retry notification for {ChildName} week {WeekNumber}/{Year}",
                    child.FirstName, weekNumber, year);
            }

            return null;
        }
        return weekLetter;
    }

    private async Task<(string? content, string? contentHash)> ValidateAndProcessWeekLetterContent(dynamic weekLetter, string childName, int weekNumber, int year)
    {
        var content = ExtractWeekLetterContent(weekLetter);
        if (string.IsNullOrEmpty(content))
        {
            _logger.LogWarning("Week letter content is empty for {ChildName}", childName);
            var _ = await _retryTrackingRepository.IncrementRetryAttemptAsync(childName, weekNumber, year);
            return (null, null);
        }

        var contentHash = ComputeContentHash(content);
        return (content, contentHash);
    }

    private async Task<bool> IsContentAlreadyPosted(Child child, string contentHash, int weekNumber, int year)
    {
        var existingPosts = await _appStateRepository.GetAppStateAsync($"last_posted_hash_{child.FirstName}");
        if (existingPosts == contentHash)
        {
            _logger.LogInformation("Week letter content unchanged for {ChildName}, marking as posted", child.FirstName);
            await _weekLetterRepository.MarkWeekLetterAsPostedAsync(child.FirstName, weekNumber, year, contentHash, true, child.Channels?.Telegram?.Enabled == true);
            return true;
        }
        return false;
    }

    private async Task PostAndMarkWeekLetter(Child child, dynamic weekLetter, string content, string contentHash, int weekNumber, int year)
    {
        await PostWeekLetter(child, weekLetter, content);

        await _weekLetterRepository.StoreWeekLetterAsync(child.FirstName, weekNumber, year, contentHash, weekLetter.ToString(), true, child.Channels?.Telegram?.Enabled == true);
        await _appStateRepository.SetAppStateAsync($"last_posted_hash_{child.FirstName}", contentHash);
        await _retryTrackingRepository.MarkRetryAsSuccessfulAsync(child.FirstName, weekNumber, year);
    }

    private string ExtractWeekLetterContent(dynamic weekLetter)
    {
        return WeekLetterContentExtractor.ExtractContent(weekLetter, _logger);
    }

    private string ComputeContentHash(string content)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }

    private Task PostWeekLetter(Child child, dynamic weekLetter, string content)
    {
        try
        {
            var ugebreve = weekLetter?["ugebreve"];
            var weekLetterTitle = "";
            if (ugebreve is JArray ugebreveArray && ugebreveArray.Count > 0)
            {
                var uge = ugebreveArray[0]?["uge"]?.ToString() ?? "";
                var klasseNavn = ugebreveArray[0]?["klasseNavn"]?.ToString() ?? "";
                weekLetterTitle = $"Uge {uge} - {klasseNavn}";
            }

            var html2MarkdownConverter = new Html2SlackMarkdownConverter();
            var markdownContent = html2MarkdownConverter.Convert(content).Replace("**", "*");


            _logger.LogInformation("Week letter posting disabled in current build for {ChildName}", child.FirstName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error posting week letter for {ChildName}", child.FirstName);
            throw;
        }

        return Task.CompletedTask;
    }

    private string FormatReminderSuccessMessage(int reminderCount, int weekNumber, List<CreatedReminderInfo> createdReminders)
    {
        var message = $"Jeg har oprettet {reminderCount} påmindelser for uge {weekNumber}:";

        // Separate current week and future reminders
        var currentWeekReminders = createdReminders.Where(r => r.IsCurrentWeek).ToList();
        var futureReminders = createdReminders.Where(r => !r.IsCurrentWeek).ToList();

        // Process current week reminders (show as day names)
        if (currentWeekReminders.Any())
        {
            var groupedByDay = currentWeekReminders
                .GroupBy(r => r.DayOfWeek ?? r.Date.ToString("dddd", new System.Globalization.CultureInfo("da-DK")))
                .OrderBy(g => currentWeekReminders.First(r => (r.DayOfWeek ?? r.Date.ToString("dddd", new System.Globalization.CultureInfo("da-DK"))) == g.Key).Date);

            foreach (var dayGroup in groupedByDay)
            {
                foreach (var reminder in dayGroup)
                {
                    var timeInfo = !string.IsNullOrEmpty(reminder.EventTime) ? $" kl. {reminder.EventTime}" : "";
                    var dayName = char.ToUpper(dayGroup.Key[0]) + dayGroup.Key.Substring(1);
                    message += $"\n• {dayName}: {reminder.Title}{timeInfo}";
                }
            }
        }

        // Process future reminders (show with actual dates)
        if (futureReminders.Any())
        {
            var orderedFutureReminders = futureReminders.OrderBy(r => r.Date);

            foreach (var reminder in orderedFutureReminders)
            {
                var timeInfo = !string.IsNullOrEmpty(reminder.EventTime) ? $" kl. {reminder.EventTime}" : "";
                var dateInfo = reminder.Date.ToString("d. MMMM", new System.Globalization.CultureInfo("da-DK"));
                message += $"\n• {dateInfo}: {reminder.Title}{timeInfo}";
            }
        }

        return message;
    }

    private async Task CheckForMissedReminders()
    {
        try
        {
            _logger.LogInformation("Checking for missed reminders on startup");

            var pendingReminders = await _reminderRepository.GetPendingRemindersAsync();

            if (pendingReminders.Count > 0)
            {
                _logger.LogWarning("Found {Count} missed reminders on startup", pendingReminders.Count);

                foreach (var reminder in pendingReminders)
                {
                    // Reminder times are stored as local time in database, use local time for display
                    var reminderLocalDateTime = reminder.RemindDate.ToDateTime(reminder.RemindTime);
                    var missedBy = DateTime.Now - reminderLocalDateTime;

                    string childInfo = !string.IsNullOrEmpty(reminder.ChildName) ? $" ({reminder.ChildName})" : "";
                    string message = $"⚠️ *Missed Reminder*{childInfo}: {reminder.Text}\n" +
                                   $"_Was scheduled for {reminderLocalDateTime:HH:mm} ({missedBy.TotalMinutes:F0} minutes ago)_";

                    // Create a modified reminder with the missed notification message for sending
                    var missedReminder = new Reminder
                    {
                        Id = reminder.Id,
                        Text = message,
                        ChildName = reminder.ChildName,
                        RemindDate = reminder.RemindDate,
                        RemindTime = reminder.RemindTime,
                        IsSent = reminder.IsSent
                    };

                    _logger.LogInformation("Sending missed reminder notification for {ChildName}: {Text}", reminder.ChildName, reminder.Text);
                    SendReminderNotification(missedReminder);
                    await _reminderRepository.MarkReminderAsSentAsync(reminder.Id);

                    _logger.LogInformation("Notified about missed reminder: {Text}", reminder.Text);
                }
            }
            else
            {
                _logger.LogInformation("No missed reminders found on startup");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking for missed reminders");
        }
    }

    private async Task CheckForMissedScheduledTasks()
    {
        try
        {
            _logger.LogInformation("Checking for missed scheduled tasks on startup");

            var tasks = await _scheduledTaskRepository.GetScheduledTasksAsync();
            var now = DateTime.UtcNow;
            var missedTasks = new List<ScheduledTask>();

            foreach (var task in tasks)
            {
                if (!task.Enabled)
                    continue;

                try
                {
                    DateTime nextRun;

                    if (task.NextRun.HasValue)
                    {
                        nextRun = task.NextRun.Value;
                    }
                    else
                    {
                        var schedule = CrontabSchedule.Parse(task.CronExpression);
                        nextRun = task.LastRun != null
                            ? schedule.GetNextOccurrence(task.LastRun.Value)
                            : schedule.GetNextOccurrence(now.AddMinutes(-_config.Scheduling.InitialOccurrenceOffsetMinutes));
                    }

                    // Check if we missed this task (NextRun is in the past and we haven't run it yet)
                    if (now > nextRun && (task.LastRun == null || task.LastRun < nextRun))
                    {
                        var missedBy = now - nextRun;
                        _logger.LogWarning("Found missed scheduled task: {TaskName}, was scheduled for {NextRun} ({MissedMinutes} minutes ago)",
                            task.Name, nextRun, missedBy.TotalMinutes);
                        missedTasks.Add(task);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error checking if task {TaskName} was missed", task.Name);
                }
            }

            if (missedTasks.Count > 0)
            {
                _logger.LogInformation("Executing {Count} missed scheduled tasks", missedTasks.Count);

                foreach (var task in missedTasks)
                {
                    try
                    {
                        _logger.LogInformation("Executing missed scheduled task: {TaskName}", task.Name);

                        task.LastRun = now;
                        task.NextRun = GetNextRunTime(task.CronExpression, now.AddMinutes(2));
                        await _scheduledTaskRepository.UpdateScheduledTaskAsync(task);
                        await ExecuteTask(task);

                        _logger.LogInformation("Successfully executed missed task: {TaskName}", task.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error executing missed scheduled task: {TaskName}", task.Name);
                    }
                }
            }
            else
            {
                _logger.LogInformation("No missed scheduled tasks found on startup");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking for missed scheduled tasks");
        }
    }

    private async Task ExecutePendingRetries()
    {
        try
        {
            var pendingRetries = await _retryTrackingRepository.GetPendingRetriesAsync();

            if (pendingRetries.Count == 0)
                return;

            _logger.LogInformation("Processing {Count} pending retries", pendingRetries.Count);

            foreach (var retry in pendingRetries)
            {
                try
                {
                    var child = _config.MinUddannelse?.Children?.FirstOrDefault(c => c.FirstName == retry.ChildName);
                    if (child == null)
                    {
                        _logger.LogWarning("Child {ChildName} not found in config, skipping retry", retry.ChildName);
                        continue;
                    }

                    var firstDayOfWeek = ISOWeek.ToDateTime(retry.Year, retry.WeekNumber, DayOfWeek.Monday);
                    var date = DateOnly.FromDateTime(firstDayOfWeek);

                    _logger.LogInformation("Retry attempt {AttemptCount} for {ChildName} week {WeekNumber}/{Year}",
                        retry.AttemptCount, retry.ChildName, retry.WeekNumber, retry.Year);

                    var weekLetter = await _weekLetterService.GetOrFetchWeekLetterAsync(child, date, true);

                    if (weekLetter != null && !WeekLetterService.IsWeekLetterEffectivelyEmpty(weekLetter))
                    {
                        await _retryTrackingRepository.MarkRetryAsSuccessfulAsync(retry.ChildName, retry.WeekNumber, retry.Year);

                        _logger.LogInformation("Week letter now available for {ChildName} week {WeekNumber}/{Year} after {AttemptCount} attempts",
                            retry.ChildName, retry.WeekNumber, retry.Year, retry.AttemptCount);

                        var successMessage = $"✅ Ugebrev for uge {retry.WeekNumber}/{retry.Year} er nu tilgængeligt!\n\n" +
                                           $"Jeg processer det nu og sender dig detaljerne om lidt.";
                        var childId = Configuration.Child.GenerateChildId(retry.ChildName);
                        var eventArgs = new ChildMessageEventArgs(childId, retry.ChildName, successMessage, "week_letter_retry_success");
                        MessageReady?.Invoke(this, eventArgs);

                        await CheckAndPostWeekLetter(child, new ScheduledTask
                        {
                            Name = "RetryProcessing",
                            NextRun = DateTime.UtcNow
                        });
                    }
                    else
                    {
                        await _retryTrackingRepository.IncrementRetryAttemptAsync(retry.ChildName, retry.WeekNumber, retry.Year);
                        _logger.LogInformation("Week letter still not available for {ChildName} week {WeekNumber}/{Year}, attempt {AttemptCount}",
                            retry.ChildName, retry.WeekNumber, retry.Year, retry.AttemptCount);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing retry for {ChildName} week {WeekNumber}/{Year}",
                        retry.ChildName, retry.WeekNumber, retry.Year);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing pending retries");
        }
    }
}
