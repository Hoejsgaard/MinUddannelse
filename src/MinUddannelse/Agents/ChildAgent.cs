using System;
using MinUddannelse.Content.WeekLetters;
using System.Threading.Tasks;
using MinUddannelse.Configuration;
using MinUddannelse.AI.Services;
using MinUddannelse.Scheduling;
using MinUddannelse.Bots;
using MinUddannelse.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace MinUddannelse.Agents;

public class ChildAgent : IChildAgent
{
    private readonly Child _child;

    public Child Child => _child;
    private readonly IOpenAiService _openAiService;
    private readonly IWeekLetterService _weekLetterService;
    private readonly bool _postWeekLettersOnStartup;
    private readonly ISchedulingService _schedulingService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private SlackInteractiveBot? _slackBot;
    private TelegramInteractiveBot? _telegramBot;
    private EventHandler<ChildWeekLetterEventArgs>? _weekLetterHandler;
    private EventHandler<ChildReminderEventArgs>? _reminderHandler;
    private EventHandler<ChildMessageEventArgs>? _messageHandler;
    private System.Timers.Timer? _botHealthCheckTimer;
    private DateTime _lastSlackFailure = DateTime.MinValue;
    private DateTime _lastTelegramFailure = DateTime.MinValue;
    private readonly TimeSpan _recoveryRetryInterval = TimeSpan.FromMinutes(5);

    public ChildAgent(
        Child child,
        IOpenAiService openAiService,
        IWeekLetterService weekLetterService,
        bool postWeekLettersOnStartup,
        ISchedulingService schedulingService,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(openAiService);
        ArgumentNullException.ThrowIfNull(weekLetterService);
        ArgumentNullException.ThrowIfNull(schedulingService);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _child = child;
        _openAiService = openAiService;
        _weekLetterService = weekLetterService;
        _postWeekLettersOnStartup = postWeekLettersOnStartup;
        _schedulingService = schedulingService;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ChildAgent>();
    }

    public async Task StartAsync()
    {
        _logger.LogInformation("Starting agent for child {ChildName}", _child.FirstName);

        await StartSlackBotAsync();
        await StartTelegramBotAsync();
        SubscribeToWeekLetterEvents();
        SubscribeToReminderEvents();
        SubscribeToMessageEvents();
        StartBotHealthCheckTimer();

        if (_postWeekLettersOnStartup)
        {
            await PostStartupWeekLetterAsync();
        }
    }

    public async Task StopAsync()
    {
        _logger.LogInformation("Stopping agent for child {ChildName}", _child.FirstName);

        if (_weekLetterHandler != null)
        {
            _schedulingService.ChildWeekLetterReady -= _weekLetterHandler;
            _weekLetterHandler = null;
        }

        if (_reminderHandler != null)
        {
            _schedulingService.ReminderReady -= _reminderHandler;
            _reminderHandler = null;
        }

        if (_messageHandler != null)
        {
            _schedulingService.MessageReady -= _messageHandler;
            _messageHandler = null;
        }

        _slackBot?.Dispose();
        _telegramBot?.Dispose();

        if (_botHealthCheckTimer != null)
        {
            _botHealthCheckTimer.Stop();
            _botHealthCheckTimer.Dispose();
            _botHealthCheckTimer = null;
        }

        await Task.CompletedTask;
    }

    private async Task StartSlackBotAsync()
    {
        if (_child.Channels?.Slack?.Enabled == true &&
            _child.Channels?.Slack?.EnableBot == true &&
            !string.IsNullOrEmpty(_child.Channels?.Slack?.ApiToken))
        {
            try
            {
                _logger.LogInformation("Starting SlackInteractiveBot for {ChildName} on channel {ChannelId}",
                    _child.FirstName, _child.Channels!.Slack!.ChannelId);

                _slackBot = new SlackInteractiveBot(
                    _child,
                    _openAiService,
                    _loggerFactory,
                    _child.Channels.Slack.EnableInteractive);

                await _slackBot.Start();

                _logger.LogInformation("SlackInteractiveBot started successfully for {ChildName}", _child.FirstName);
            }
            catch (Exception ex)
            {
                _lastSlackFailure = DateTime.Now;
                _logger.LogError(ex, "Failed to start Slack bot for {ChildName} - continuing with degraded functionality. " +
                                    "Will attempt automatic recovery in {Minutes} minutes.", _child.FirstName, _recoveryRetryInterval.TotalMinutes);
                _slackBot = null; // Ensure bot is null on failure
            }
        }
    }

    private async Task StartTelegramBotAsync()
    {
        if (_child.Channels?.Telegram?.Enabled == true &&
            _child.Channels?.Telegram?.EnableBot == true &&
            !string.IsNullOrEmpty(_child.Channels?.Telegram?.Token))
        {
            try
            {
                _logger.LogInformation("Starting TelegramInteractiveBot for {ChildName}", _child.FirstName);

                _telegramBot = new TelegramInteractiveBot(
                    _child,
                    _openAiService,
                    _loggerFactory,
                    _child.Channels.Telegram.EnableInteractive);

                await _telegramBot.Start();

                _logger.LogInformation("TelegramInteractiveBot started successfully for {ChildName}", _child.FirstName);
            }
            catch (Exception ex)
            {
                _lastTelegramFailure = DateTime.Now;
                _logger.LogError(ex, "Failed to start Telegram bot for {ChildName} - continuing with degraded functionality. " +
                                    "Will attempt automatic recovery in {Minutes} minutes.", _child.FirstName, _recoveryRetryInterval.TotalMinutes);
                _telegramBot = null; // Ensure bot is null on failure
            }
        }
        else
        {
            _logger.LogInformation("Telegram bot disabled for {ChildName} - not configured or not enabled", _child.FirstName);
        }
    }

    private void SubscribeToWeekLetterEvents()
    {
        _weekLetterHandler = async (sender, args) =>
        {
            var handler = new ChildWeekLetterHandler(_child, _loggerFactory);
            await handler.HandleWeekLetterEventAsync(args, _slackBot, _telegramBot);
        };
        _schedulingService.ChildWeekLetterReady += _weekLetterHandler;
    }

    private void SubscribeToReminderEvents()
    {
        _reminderHandler = async (sender, args) =>
        {
            if (!args.ChildId.Equals(_child.GetChildId(), StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var formattedReminderText = FormatReminderText(args);
                await SendReminderMessageAsync(formattedReminderText);
                _logger.LogInformation("Handled reminder {ReminderId} for {ChildName}",
                    args.ReminderId, _child.FirstName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to handle reminder {ReminderId} for {ChildName}",
                    args.ReminderId, _child.FirstName);
            }
        };
        _schedulingService.ReminderReady += _reminderHandler;
    }

    private void SubscribeToMessageEvents()
    {
        _messageHandler = async (sender, args) =>
        {
            if (!args.ChildId.Equals(_child.GetChildId(), StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                await SendReminderMessageAsync(args.Message);
                _logger.LogInformation("Handled {MessageType} message for {ChildName}",
                    args.MessageType, _child.FirstName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to handle {MessageType} message for {ChildName}",
                    args.MessageType, _child.FirstName);
            }
        };
        _schedulingService.MessageReady += _messageHandler;
    }

    private async Task PostStartupWeekLetterAsync()
    {
        if (!(_schedulingService is SchedulingService startupSchedService))
            return;

        _logger.LogInformation("Posting current week letters on startup for {ChildName}", _child.FirstName);

        var now = DateTime.Now;
        var weekNumber = System.Globalization.ISOWeek.GetWeekOfYear(now);
        var year = now.Year;

        try
        {
            var date = DateOnly.FromDateTime(now.AddDays(-7 * (System.Globalization.ISOWeek.GetWeekOfYear(now) - weekNumber)));
            var weekLetter = await _weekLetterService.GetOrFetchWeekLetterAsync(_child, date, true);

            if (weekLetter != null)
            {
                _logger.LogInformation("Emitting week letter event for {ChildName} (week {WeekNumber}/{Year})",
                    _child.FirstName, weekNumber, year);

                var childId = _child.GetChildId();
                var eventArgs = new ChildWeekLetterEventArgs(
                    childId,
                    _child.FirstName,
                    weekNumber,
                    year,
                    weekLetter);

                startupSchedService.TriggerChildWeekLetterReady(eventArgs);
            }
            else
            {
                _logger.LogWarning("No week letter found for {ChildName} (week {WeekNumber}/{Year})",
                    _child.FirstName, weekNumber, year);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error posting week letter on startup for {ChildName}", _child.FirstName);
        }
    }

    private string FormatReminderText(ChildReminderEventArgs args)
    {
        var reminderText = args.ReminderText;
        var reminderDateTime = args.RemindDate.ToDateTime(args.RemindTime);
        var now = DateTime.Now;

        var isDelayed = now > reminderDateTime;

        if (isDelayed)
        {
            var delay = now - reminderDateTime;

            if (delay.TotalHours > 1)
            {
                var originalDate = reminderDateTime;
                var danishMonths = new[] { "", "jan", "feb", "mar", "apr", "maj", "jun",
                                         "jul", "aug", "sep", "okt", "nov", "dec" };
                var monthName = danishMonths[originalDate.Month];

                var danishDays = new[] { "søndag", "mandag", "tirsdag", "onsdag", "torsdag", "fredag", "lørdag" };
                var dayName = danishDays[(int)originalDate.DayOfWeek];

                var delayDisclaimer = $"Forsinket notifikation - skulle være afsendt {dayName} d. {originalDate.Day}. {monthName} kl. {originalDate:HH:mm}";

                var cleanedReminderText = StripDayNamesFromReminderText(reminderText);

                return $"{delayDisclaimer}\n\n{cleanedReminderText}";
            }

            return reminderText;
        }

        return reminderText;
    }

    private string StripDayNamesFromReminderText(string reminderText)
    {
        var danishDays = new[] { "søndag", "mandag", "tirsdag", "onsdag", "torsdag", "fredag", "lørdag" };

        var result = reminderText;

        foreach (var dayName in danishDays)
        {
            result = result.Replace($"({dayName})", "");
        }

        foreach (var dayName in danishDays)
        {
            result = result.Replace($"{dayName} fra", "fra");
            result = result.Replace($"{dayName} kl", "kl");
            result = result.Replace($"på {dayName}", "i dag");
        }

        result = result.Replace("  ", " ").Trim();

        return result;
    }

    private string AddDayNameToReminderText(string reminderText, DateTime originalDateTime)
    {
        var danishDays = new[] { "søndag", "mandag", "tirsdag", "onsdag", "torsdag", "fredag", "lørdag" };
        var dayName = danishDays[(int)originalDateTime.DayOfWeek];

        if (reminderText.Contains("i dag"))
        {
            return reminderText.Replace("i dag", $"i dag ({dayName})");
        }

        if (reminderText.StartsWith("Husk"))
        {
            return reminderText.Replace("Husk", $"Husk ({dayName})");
        }

        return $"({dayName}) {reminderText}";
    }

    public async Task SendReminderMessageAsync(string message)
    {
        try
        {
            var tasks = new List<Task>();

            if (_slackBot != null)
            {
                tasks.Add(_slackBot.SendMessageToSlack(message));
            }

            if (_telegramBot != null)
            {
                tasks.Add(_telegramBot.SendMessageToTelegram(message));
            }

            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks);
                _logger.LogInformation("Sent reminder message to {ChildName} on {Count} platforms", _child.FirstName, tasks.Count);
            }
            else
            {
                _logger.LogWarning("No bots available to send reminder message for {ChildName}", _child.FirstName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending reminder message for {ChildName}", _child.FirstName);
        }
    }

    private void StartBotHealthCheckTimer()
    {
        _botHealthCheckTimer = new System.Timers.Timer(_recoveryRetryInterval.TotalMilliseconds);
        _botHealthCheckTimer.Elapsed += async (sender, e) => await PerformBotHealthCheckAsync();
        _botHealthCheckTimer.AutoReset = true;
        _botHealthCheckTimer.Start();

        _logger.LogInformation("Started bot health check timer for {ChildName} - checking every {Minutes} minutes",
                              _child.FirstName, _recoveryRetryInterval.TotalMinutes);
    }

    private async Task PerformBotHealthCheckAsync()
    {
        try
        {
            // Check if Slack bot needs recovery
            if (_slackBot == null &&
                _child.Channels?.Slack?.Enabled == true &&
                _child.Channels?.Slack?.EnableBot == true &&
                !string.IsNullOrEmpty(_child.Channels?.Slack?.ApiToken) &&
                _lastSlackFailure != DateTime.MinValue &&
                DateTime.Now - _lastSlackFailure >= _recoveryRetryInterval)
            {
                _logger.LogInformation("Attempting Slack bot recovery for {ChildName}", _child.FirstName);
                await StartSlackBotAsync();

                if (_slackBot != null)
                {
                    _logger.LogInformation("✅ Slack bot recovery successful for {ChildName}", _child.FirstName);
                    _lastSlackFailure = DateTime.MinValue; // Reset failure time on success
                }
            }

            // Check if Telegram bot needs recovery
            if (_telegramBot == null &&
                _child.Channels?.Telegram?.Enabled == true &&
                _child.Channels?.Telegram?.EnableBot == true &&
                !string.IsNullOrEmpty(_child.Channels?.Telegram?.Token) &&
                _lastTelegramFailure != DateTime.MinValue &&
                DateTime.Now - _lastTelegramFailure >= _recoveryRetryInterval)
            {
                _logger.LogInformation("Attempting Telegram bot recovery for {ChildName}", _child.FirstName);
                await StartTelegramBotAsync();

                if (_telegramBot != null)
                {
                    _logger.LogInformation("✅ Telegram bot recovery successful for {ChildName}", _child.FirstName);
                    _lastTelegramFailure = DateTime.MinValue; // Reset failure time on success
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during bot health check for {ChildName}", _child.FirstName);
        }
    }
}
