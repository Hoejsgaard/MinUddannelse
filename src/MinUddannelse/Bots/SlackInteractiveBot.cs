using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using MinUddannelse.Configuration;
using MinUddannelse.Client;
using MinUddannelse.AI.Services;
using MinUddannelse.Content.WeekLetters;

namespace MinUddannelse.Bots;

public class SlackInteractiveBot : IDisposable
{
    private readonly Child _child;
    private readonly IOpenAiService _aiService;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _enableInteractive;

    public string AssignedChildName => _child.FirstName;

    private Timer? _pollingTimer;
    private Timer? _cleanupTimer;
    private string _lastTimestamp = "0";
    private readonly object _lockObject = new object();
    private int _pollingInProgress;

    private readonly ConcurrentDictionary<string, byte> _sentMessageIds = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, DateTime> _messageTimestamps = new ConcurrentDictionary<string, DateTime>();

    public SlackInteractiveBot(
        Child child,
        IOpenAiService aiService,
        ILoggerFactory loggerFactory,
        bool enableInteractive = true,
        HttpClient? httpClient = null)
    {
        _child = child ?? throw new ArgumentNullException(nameof(child));
        _aiService = aiService ?? throw new ArgumentNullException(nameof(aiService));
        _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger<SlackInteractiveBot>();
        _httpClient = httpClient ?? new HttpClient();
        _enableInteractive = enableInteractive;

        if (httpClient == null)
        {
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }
    }

    public async Task Start()
    {
        if (string.IsNullOrEmpty(_child.Channels?.Slack?.ApiToken))
        {
            _logger.LogError("Cannot start Slack bot for {ChildName}: API token is missing", _child.FirstName);
            return;
        }

        if (string.IsNullOrEmpty(_child.Channels?.Slack?.ChannelId))
        {
            _logger.LogError("Cannot start Slack bot for {ChildName}: Channel ID is missing", _child.FirstName);
            return;
        }

        _logger.LogInformation("Starting Slack bot for child: {ChildName}", _child.FirstName);

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _child.Channels.Slack.ApiToken);

        _lastTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        _logger.LogInformation("Initial timestamp set to: {Timestamp}", _lastTimestamp + ".000000");

        if (_enableInteractive)
        {
            int pollingInterval = _child.Channels.Slack.PollingIntervalSeconds * 1000;
            _pollingTimer = new Timer(async _ => await PollForMessages(), null, pollingInterval, pollingInterval);
            _logger.LogInformation("Slack polling started - checking every {Seconds} seconds", _child.Channels.Slack.PollingIntervalSeconds);
        }
        else
        {
            _logger.LogInformation("Slack interactive polling disabled - bot will only send messages");
        }

        int cleanupInterval = _child.Channels.Slack.CleanupIntervalHours;
        _cleanupTimer = new Timer(_ => CleanupOldMessages(), null, TimeSpan.FromHours(cleanupInterval), TimeSpan.FromHours(cleanupInterval));

        _logger.LogInformation("Slack cleanup timer started - running every {Hours} hour(s)", cleanupInterval);

        if (_child.Channels.Slack.SendStartupMessage)
        {
            await SendMessageToSlack($"Bot for {_child.FirstName} is now online and ready to help!");
        }
    }

    private async Task PollForMessages()
    {
        if (Interlocked.Exchange(ref _pollingInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            var url = $"https://slack.com/api/conversations.history?channel={_child.Channels?.Slack?.ChannelId}&oldest={_lastTimestamp}";
            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to poll Slack messages. Status: {StatusCode}", response.StatusCode);
                return;
            }

            var content = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(content);

            if (json["ok"]?.Value<bool>() != true)
            {
                _logger.LogError("Slack API returned error: {Error}", json["error"]?.ToString());
                return;
            }

            var messages = json["messages"] as JArray;
            if (messages == null || messages.Count == 0)
            {
                return;
            }

            foreach (var message in messages.OrderBy(m => m["ts"]?.ToString()))
            {
                await ProcessMessage(message as JObject);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling for Slack messages");
        }
        finally
        {
            Interlocked.Exchange(ref _pollingInProgress, 0);
        }
    }

    private async Task ProcessMessage(JObject? message)
    {
        if (message == null) return;

        var messageId = message["ts"]?.ToString();
        var userId = message["user"]?.ToString();
        var text = message["text"]?.ToString();
        var subtype = message["subtype"]?.ToString();

        if (string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(text))
        {
            return;
        }

        _lastTimestamp = messageId;

        if (_sentMessageIds.ContainsKey(messageId))
        {
            return;
        }

        if (subtype == "bot_message" || message["bot_id"] != null)
        {
            return;
        }

        _logger.LogInformation("Processing message for {ChildName} from user {UserId}: {Text}",
            _child.FirstName, userId, text);

        try
        {
            var response = await _aiService.GetResponseAsync(_child, text);

            await SendMessageToSlack(response ?? "I couldn't process your request.");

            _logger.LogInformation("Processed message for child {ChildName} successfully", _child.FirstName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message for child {ChildName}", _child.FirstName);
            await SendMessageToSlack($"Sorry, I encountered an error processing your request about {_child.FirstName}.");
        }
    }

    public async Task SendMessageToSlack(string text, string? threadTs = null)
    {
        var payload = new
        {
            channel = _child.Channels?.Slack?.ChannelId,
            text = text,
            thread_ts = threadTs
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        try
        {
            var response = await _httpClient.PostAsync(
                "https://slack.com/api/chat.postMessage",
                content);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                var responseJson = JObject.Parse(responseContent);
                var messageId = responseJson["ts"]?.ToString();

                if (!string.IsNullOrEmpty(messageId))
                {
                    _sentMessageIds.TryAdd(messageId, 0);
                    _messageTimestamps.TryAdd(messageId, DateTime.UtcNow);
                }

                _logger.LogInformation("Message sent successfully to Slack");
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to send message to Slack. Status: {StatusCode}, Content: {Content}",
                    response.StatusCode, errorContent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending message to Slack");
        }
    }

    private void CleanupOldMessages()
    {
        var cutoff = DateTime.UtcNow.AddHours(-2);
        var toRemove = _messageTimestamps
            .Where(kvp => kvp.Value < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var messageId in toRemove)
        {
            _sentMessageIds.TryRemove(messageId, out _);
            _messageTimestamps.TryRemove(messageId, out _);
        }

        if (toRemove.Count > 0)
        {
            _logger.LogDebug("Cleaned up {Count} old message IDs", toRemove.Count);
        }
    }

    public void Stop()
    {
        _pollingTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _cleanupTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _logger.LogInformation("Slack bot stopped");
    }

    public void Dispose()
    {
        Stop();
        _pollingTimer?.Dispose();
        _cleanupTimer?.Dispose();
        _httpClient?.Dispose();
    }
}
