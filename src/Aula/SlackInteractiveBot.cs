using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Aula;

public class SlackInteractiveBot
{
    private readonly IAgentService _agentService;
    private readonly Config _config;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, Child> _childrenByName;
    private bool _isRunning;
    private Timer? _pollingTimer;
    private string _lastTimestamp = "0"; // Start from the beginning of time
    private readonly object _lockObject = new object();
    private int _pollingInProgress = 0;
    private readonly HashSet<string> _postedWeekLetterHashes = new HashSet<string>();
    // Track our own message IDs to avoid processing them
    private readonly HashSet<string> _sentMessageIds = new HashSet<string>();
    // Keep track of when messages were sent to allow cleanup
    private readonly Dictionary<string, DateTime> _messageTimestamps = new Dictionary<string, DateTime>();
    private readonly HashSet<string> _englishWords = new HashSet<string> { "what", "when", "how", "is", "does", "do", "can", "will", "has", "have", "had", "show", "get", "tell", "please", "thanks", "thank", "you", "hello", "hi" };
    private readonly HashSet<string> _danishWords = new HashSet<string> { "hvad", "hvornår", "hvordan", "er", "gør", "kan", "vil", "har", "havde", "vis", "få", "fortæl", "venligst", "tak", "du", "dig", "hej", "hallo", "goddag" };

    // Conversation context tracking
    private class ConversationContext
    {
        public string? LastChildName { get; set; }
        public bool WasAboutToday { get; set; }
        public bool WasAboutTomorrow { get; set; }
        public bool WasAboutHomework { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;

        public bool IsStillValid => (DateTime.Now - Timestamp).TotalMinutes < 10; // Context expires after 10 minutes

        public override string ToString()
        {
            return $"Child: {LastChildName ?? "none"}, Today: {WasAboutToday}, Tomorrow: {WasAboutTomorrow}, Homework: {WasAboutHomework}, Age: {(DateTime.Now - Timestamp).TotalMinutes:F1} minutes";
        }
    }

    private ConversationContext _conversationContext = new ConversationContext();

    private void UpdateConversationContext(string? childName, bool isAboutToday, bool isAboutTomorrow, bool isAboutHomework)
    {
        _conversationContext = new ConversationContext
        {
            LastChildName = childName,
            WasAboutToday = isAboutToday,
            WasAboutTomorrow = isAboutTomorrow,
            WasAboutHomework = isAboutHomework,
            Timestamp = DateTime.Now
        };

        _logger.LogInformation("Updated conversation context: {Context}", _conversationContext);
    }

    private readonly ISupabaseService _supabaseService;

    public SlackInteractiveBot(
        IAgentService agentService,
        Config config,
        ILoggerFactory loggerFactory,
        ISupabaseService supabaseService)
    {
        _agentService = agentService;
        _config = config;
        _logger = loggerFactory.CreateLogger<SlackInteractiveBot>();
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(30); // Add 30 second timeout
        _supabaseService = supabaseService;
        _childrenByName = _config.Children.ToDictionary(
            c => c.FirstName.ToLowerInvariant(),
            c => c);
    }

    public async Task Start()
    {
        if (string.IsNullOrEmpty(_config.Slack.ApiToken))
        {
            _logger.LogError("Cannot start Slack bot: API token is missing");
            return;
        }

        if (string.IsNullOrEmpty(_config.Slack.ChannelId))
        {
            _logger.LogError("Cannot start Slack bot: Channel ID is missing");
            return;
        }

        _logger.LogInformation("Starting Slack polling bot");

        // Configure the HTTP client for Slack API
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.Slack.ApiToken);

        // Set the timestamp to now so we don't process old messages
        // Slack uses a timestamp format like "1234567890.123456"
        var unixTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _lastTimestamp = $"{unixTimestamp}.000000";
        _logger.LogInformation("Initial timestamp set to: {Timestamp}", _lastTimestamp);

        // Start polling
        _isRunning = true;
        _pollingTimer = new Timer(PollMessages, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));

        // Start message ID cleanup timer (run every hour)
        var cleanupTimer = new Timer(CleanupOldMessageIds, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        // Build a list of available children (first names only)
        string childrenList = string.Join(" og ", _childrenByName.Values.Select(c => c.FirstName.Split(' ')[0]));

        // Get the current week number
        int weekNumber = System.Globalization.ISOWeek.GetWeekOfYear(DateTime.Now);

        // Send welcome message in Danish with children info
        await SendMessageInternal($"Jeg er online og har ugeplan for {childrenList} for Uge {weekNumber}");

        _logger.LogInformation("Slack polling bot started");
    }

    public void Stop()
    {
        _isRunning = false;
        _pollingTimer?.Dispose();
        _logger.LogInformation("Slack polling bot stopped");
    }

    private async void PollMessages(object? state)
    {
        // Don't use locks with async/await as it can lead to deadlocks
        // Instead, use a simple flag to prevent concurrent executions
        if (!_isRunning || Interlocked.Exchange(ref _pollingInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            // Build the API URL for conversations.history
            // Add a small buffer to the timestamp to avoid duplicate messages
            var adjustedTimestamp = _lastTimestamp;
            if (!string.IsNullOrEmpty(_lastTimestamp) && _lastTimestamp != "0")
            {
                // Slack timestamps are in the format "1234567890.123456"
                // We need to ensure we're handling them correctly
                if (_lastTimestamp.Contains("."))
                {
                    // Already in correct format, add a tiny fraction to avoid duplicates
                    adjustedTimestamp = _lastTimestamp;
                }
                else if (double.TryParse(_lastTimestamp, out double ts))
                {
                    // Convert to proper Slack timestamp format if needed
                    adjustedTimestamp = ts.ToString("0.000000");
                }
            }

            // Removed noisy polling log - only log when there are actual messages
            var url = $"https://slack.com/api/conversations.history?channel={_config.Slack.ChannelId}&oldest={adjustedTimestamp}&limit=10";

            // Make the API call
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to fetch messages: HTTP {StatusCode}", response.StatusCode);
                return;
            }

            // Parse the response
            var content = await response.Content.ReadAsStringAsync();
            var data = JObject.Parse(content);

            if (data["ok"]?.Value<bool>() != true)
            {
                string error = data["error"]?.ToString() ?? "unknown error";

                // Handle the not_in_channel error
                if (error == "not_in_channel")
                {
                    _logger.LogWarning("Bot is not in the channel. Attempting to join...");
                    await JoinChannel();
                    return;
                }

                _logger.LogError("Failed to fetch messages: {Error}", error);
                return;
            }

            // Get the messages
            var messages = data["messages"] as JArray;
            if (messages == null || !messages.Any())
            {
                return;
            }

            // Only log if there are actual new user messages (removed spam)

            // Get actual new messages (not from the bot)
            var userMessages = messages.Where(m =>
            {
                // Skip messages we sent ourselves (by checking the ID)
                string messageId = m["ts"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(messageId) && _sentMessageIds.Contains(messageId))
                {
                    // Skip our own message (removed log spam)
                    return false;
                }

                // Skip bot messages
                if (m["subtype"]?.ToString() == "bot_message" || m["bot_id"] != null)
                {
                    return false;
                }

                // Only include messages with a user ID
                return !string.IsNullOrEmpty(m["user"]?.ToString());
            }).ToList();

            if (!userMessages.Any())
            {
                return;
            }

            _logger.LogInformation("Found {Count} new user messages", userMessages.Count);

            // Keep track of the latest timestamp
            string latestTimestamp = _lastTimestamp;

            // Process messages in chronological order (oldest first)
            foreach (var message in userMessages.OrderBy(m => m["ts"]?.ToString()))
            {
                // Process the message immediately with higher priority
                await ProcessMessage(message["text"]?.ToString() ?? "");

                // Update the latest timestamp if this message is newer
                string messageTs = message["ts"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(messageTs) &&
                    (string.IsNullOrEmpty(latestTimestamp) ||
                     string.Compare(messageTs, latestTimestamp) > 0))
                {
                    latestTimestamp = messageTs;
                }
            }

            // Update the timestamp to the latest message
            if (!string.IsNullOrEmpty(latestTimestamp) && latestTimestamp != _lastTimestamp)
            {
                _lastTimestamp = latestTimestamp;
                _logger.LogInformation("Updated last timestamp to {Timestamp}", _lastTimestamp);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling Slack messages");
        }
        finally
        {
            // Reset the flag to allow the next polling operation
            Interlocked.Exchange(ref _pollingInProgress, 0);
        }
    }

    private async Task ProcessMessage(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _logger.LogInformation("Processing message: {Text}", text);

        // Skip system messages and announcements that don't need a response
        if (text.Contains("has joined the channel") ||
            text.Contains("added an integration") ||
            text.Contains("added to the channel") ||
            text.StartsWith("<http"))
        {
            _logger.LogInformation("Skipping system message or announcement");
            return;
        }

        // Detect language (Danish or English)
        bool isEnglish = DetectLanguage(text) == "en";

        // Check for help command first
        if (await TryHandleHelpCommand(text, isEnglish))
        {
            return;
        }

        // Use the new tool-based processing that can handle both tools and regular questions
        string contextKey = $"slack-{_config.Slack.ChannelId}";
        string response = await _agentService.ProcessQueryWithToolsAsync(text, contextKey, ChatInterface.Slack);
        await SendMessageInternal(response);
    }

    private bool IsFollowUpQuestion(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        text = text.ToLowerInvariant();

        // Log the current text and conversation context
        _logger.LogInformation("Checking if '{Text}' is a follow-up question. Context valid: {IsValid}",
            text, _conversationContext.IsStillValid);

        // If the conversation context has expired, it's not a follow-up
        if (!_conversationContext.IsStillValid)
        {
            return false;
        }

        // Check for explicit follow-up phrases
        bool hasFollowUpPhrase = text.Contains("hvad med") ||
                               text.Contains("what about") ||
                               text.Contains("how about") ||
                               text.Contains("hvordan med") ||
                               text.StartsWith("og ") ||
                               text.StartsWith("and ") ||
                               text.Contains("og hvad") ||
                               text.Contains("and what") ||
                               text.Contains("også") ||
                               text.Contains("also") ||
                               text.Contains("likewise") ||
                               text == "og?" ||
                               text == "and?";

        // Check if this is a short message
        bool isShortMessage = text.Length < 15;

        // Check if the message contains a child name
        bool hasChildName = false;
        foreach (var childName in _childrenByName.Keys)
        {
            if (text.Contains(childName.ToLowerInvariant()))
            {
                hasChildName = true;
                break;
            }
        }

        // Also check for first names
        if (!hasChildName)
        {
            foreach (var child in _childrenByName.Values)
            {
                string firstName = child.FirstName.Split(' ')[0].ToLowerInvariant();
                if (text.Contains(firstName.ToLowerInvariant()))
                {
                    hasChildName = true;
                    break;
                }
            }
        }

        // Check if the message contains time references
        bool hasTimeReference = text.Contains("today") || text.Contains("tomorrow") ||
                              text.Contains("i dag") || text.Contains("i morgen");

        // Special case for very short messages that are likely follow-ups
        if (isShortMessage && (text.Contains("?") || text == "ok" || text == "okay"))
        {
            _logger.LogInformation("Detected likely follow-up based on short message: {Text}", text);
            return true;
        }

        bool result = hasFollowUpPhrase || (isShortMessage && hasChildName && !hasTimeReference);

        if (result)
        {
            _logger.LogInformation("Detected follow-up question: {Text}", text);
        }

        return result;
    }

    private string DetectLanguage(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "da"; // Default to Danish if no text
        }

        string lowerText = text.ToLowerInvariant();

        // Count English and Danish words
        int englishWordCount = 0;
        int danishWordCount = 0;

        foreach (var word in lowerText.Split(' ', ',', '.', '!', '?', ':', ';', '-', '(', ')', '[', ']', '{', '}'))
        {
            string cleanWord = word.Trim();
            if (string.IsNullOrEmpty(cleanWord))
            {
                continue;
            }

            if (_englishWords.Contains(cleanWord))
            {
                englishWordCount++;
            }

            if (_danishWords.Contains(cleanWord))
            {
                danishWordCount++;
            }
        }

        _logger.LogInformation("🔍 TRACKING: Language detection - English words: {EnglishCount}, Danish words: {DanishCount}",
            englishWordCount, danishWordCount);

        // If we have more Danish words, or equal but the text contains Danish-specific characters, use Danish
        if (danishWordCount > englishWordCount ||
            (danishWordCount == englishWordCount &&
             (lowerText.Contains('æ') || lowerText.Contains('ø') || lowerText.Contains('å'))))
        {
            return "da";
        }

        return "en";
    }

    private string? ExtractChildName(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        text = text.ToLowerInvariant();

        // For very short follow-up questions with no clear child name, use the last child from context
        if (text.Length < 15 &&
            (_conversationContext.IsStillValid && _conversationContext.LastChildName != null) &&
            (text.Contains("what about") || text.Contains("how about") ||
             text.Contains("hvad med") || text.Contains("hvordan med") ||
             text.StartsWith("og") || text.StartsWith("and")))
        {
            // Try to extract a different child name from the follow-up
            foreach (var childName in _childrenByName.Keys)
            {
                // Use word boundary to avoid partial matches
                if (Regex.IsMatch(text, $@"\b{Regex.Escape(childName)}\b", RegexOptions.IgnoreCase))
                {
                    _logger.LogInformation("Found child name in follow-up: {ChildName}", childName);
                    return childName;
                }
            }

            // Check for first names only in follow-up questions
            foreach (var child in _childrenByName.Values)
            {
                string firstName = child.FirstName.Split(' ')[0].ToLowerInvariant();
                // Use word boundary to avoid partial matches
                if (Regex.IsMatch(text, $@"\b{Regex.Escape(firstName)}\b", RegexOptions.IgnoreCase))
                {
                    string matchedKey = _childrenByName.Keys.FirstOrDefault(k =>
                        k.StartsWith(firstName, StringComparison.OrdinalIgnoreCase)) ?? "";
                    _logger.LogInformation("Found first name in follow-up: {FirstName} -> {ChildName}", firstName, matchedKey);
                    return matchedKey;
                }
            }

            // If no child name found in the follow-up, use the last child from context
            _logger.LogInformation("No child name in follow-up, using context: {ChildName}", _conversationContext.LastChildName);
            return _conversationContext.LastChildName;
        }

        // Check for "og hvad med X" or "and what about X" patterns
        string[] followUpPhrases = { "hvad med", "what about", "how about", "hvordan med", "og hvad", "and what" };
        foreach (var phrase in followUpPhrases)
        {
            int index = text.IndexOf(phrase);
            if (index >= 0)
            {
                string afterPhrase = text.Substring(index + phrase.Length).Trim();
                _logger.LogInformation("Follow-up phrase detected: '{Phrase}', text after: '{AfterPhrase}'", phrase, afterPhrase);

                // First check for full names
                foreach (var childName in _childrenByName.Keys)
                {
                    if (Regex.IsMatch(afterPhrase, $@"\b{Regex.Escape(childName)}\b", RegexOptions.IgnoreCase))
                    {
                        _logger.LogInformation("Found child name after follow-up phrase: {ChildName}", childName);
                        return childName;
                    }
                }

                // Then check for first names
                foreach (var child in _childrenByName.Values)
                {
                    string firstName = child.FirstName.Split(' ')[0].ToLowerInvariant();
                    if (Regex.IsMatch(afterPhrase, $@"\b{Regex.Escape(firstName)}\b", RegexOptions.IgnoreCase))
                    {
                        string matchedKey = _childrenByName.Keys.FirstOrDefault(k =>
                            k.StartsWith(firstName, StringComparison.OrdinalIgnoreCase)) ?? "";
                        _logger.LogInformation("Found first name after follow-up phrase: {FirstName} -> {ChildName}", firstName, matchedKey);
                        return matchedKey;
                    }
                }
            }
        }

        // Standard child name extraction - check for each child name in the text
        foreach (var childName in _childrenByName.Keys)
        {
            // Use word boundary to avoid partial matches
            if (Regex.IsMatch(text, $@"\b{Regex.Escape(childName)}\b", RegexOptions.IgnoreCase))
            {
                _logger.LogInformation("Found full child name in text: {ChildName}", childName);
                return childName;
            }
        }

        // Check for first names only
        foreach (var child in _childrenByName.Values)
        {
            string firstName = child.FirstName.Split(' ')[0].ToLowerInvariant();
            // Use word boundary to avoid partial matches
            if (Regex.IsMatch(text, $@"\b{Regex.Escape(firstName)}\b", RegexOptions.IgnoreCase))
            {
                string matchedKey = _childrenByName.Keys.FirstOrDefault(k =>
                    k.StartsWith(firstName, StringComparison.OrdinalIgnoreCase)) ?? "";
                _logger.LogInformation("Found first name in text: {FirstName} -> {ChildName}", firstName, matchedKey);
                return matchedKey;
            }
        }

        _logger.LogInformation("No child name found in text");
        return null;
    }

    private async Task<bool> TryHandleWeekLetterRequest(string text, bool isEnglish = false)
    {
        // Check if this is a request for a week letter
        var match = Regex.Match(text, @"(vis|show|få|get) (ugebrev|week letter) (?:for|til) (\w+)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }

        string childName = match.Groups[3].Value;

        // Find the child by name
        if (!_childrenByName.TryGetValue(childName.ToLowerInvariant(), out var child))
        {
            string notFoundMessage = isEnglish
                ? $"I don't know a child named {childName}. Available children are: {string.Join(", ", _childrenByName.Keys)}"
                : $"Jeg kender ikke et barn ved navn {childName}. Tilgængelige børn er: {string.Join(", ", _childrenByName.Keys)}";

            await SendMessageInternal(notFoundMessage);
            return true;
        }

        try
        {
            // Get the week letter for the child
            var weekLetter = await _agentService.GetWeekLetterAsync(child, DateOnly.FromDateTime(DateTime.Today), true);

            // Extract the content and post it
            var weekLetterContent = weekLetter["ugebreve"]?[0]?["indhold"]?.ToString() ?? "";
            var weekLetterTitle = $"Uge {weekLetter["ugebreve"]?[0]?["uge"]?.ToString() ?? ""} - {weekLetter["ugebreve"]?[0]?["klasseNavn"]?.ToString() ?? ""}";

            // Convert HTML to markdown
            var html2MarkdownConverter = new Html2SlackMarkdownConverter();
            var markdownContent = html2MarkdownConverter.Convert(weekLetterContent).Replace("**", "*");

            await PostWeekLetter(child.FirstName, markdownContent, weekLetterTitle);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting week letter for child {ChildName}", childName);
            string errorMessage = isEnglish
                ? $"Sorry, I couldn't retrieve the week letter for {childName} at the moment."
                : $"Beklager, jeg kunne ikke hente ugebrevet for {childName} i øjeblikket.";

            await SendMessageInternal(errorMessage);
            return true;
        }
    }

    private async Task<JObject> GetConversationHistory()
    {
        using var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/conversations.history");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.Slack.ApiToken);

        var parameters = new Dictionary<string, string>
        {
            { "channel", _config.Slack.ChannelId },
            { "limit", "50" }
        };

        if (!string.IsNullOrEmpty(_lastTimestamp) && _lastTimestamp != "0")
        {
            parameters.Add("oldest", _lastTimestamp);
        }

        var content = new StringContent(JsonConvert.SerializeObject(parameters), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content = content;

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync();
        return JObject.Parse(responseContent);
    }

    public async Task SendMessage(string text)
    {
        await SendMessageInternal(text);
    }

    private async Task SendMessageInternal(string text)
    {
        try
        {
            _logger.LogInformation("Sending message to Slack");

            var payload = new
            {
                channel = _config.Slack.ChannelId,
                text = text
            };

            var content = new StringContent(
                JsonConvert.SerializeObject(payload),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.PostAsync("https://slack.com/api/chat.postMessage", content);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to send message: HTTP {StatusCode}", response.StatusCode);
                return;
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            var data = JObject.Parse(responseContent);

            if (data["ok"]?.Value<bool>() != true)
            {
                _logger.LogError("Failed to send message: {Error}", data["error"]);
            }
            else
            {
                // Store the message ID to avoid processing it later
                string messageId = data["ts"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(messageId))
                {
                    _sentMessageIds.Add(messageId);
                    _messageTimestamps[messageId] = DateTime.UtcNow;
                    _logger.LogInformation("Stored sent message ID: {MessageId}", messageId);
                }
                _logger.LogInformation("Message sent successfully");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending message to Slack");
        }
    }

    private async Task JoinChannel()
    {
        try
        {
            // Create the payload to join the channel
            var payload = new
            {
                channel = _config.Slack.ChannelId
            };

            // Serialize to JSON
            var content = new StringContent(
                JsonConvert.SerializeObject(payload),
                Encoding.UTF8,
                "application/json");

            // Send to Slack API
            var response = await _httpClient.PostAsync("https://slack.com/api/conversations.join", content);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to join channel: HTTP {StatusCode}", response.StatusCode);
                return;
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            var data = JObject.Parse(responseContent);

            if (data["ok"]?.Value<bool>() != true)
            {
                _logger.LogError("Failed to join channel: {Error}", data["error"]?.ToString());

                // If we can't join, send a message to the user about it
                await SendMessageInternal("I need to be invited to this channel. Please use `/invite @YourBotName` in the channel.");
            }
            else
            {
                _logger.LogInformation("Successfully joined channel");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error joining channel");
        }
    }

    public async Task PostWeekLetter(string childName, string weekLetter, string weekLetterTitle)
    {
        if (string.IsNullOrEmpty(weekLetter))
        {
            _logger.LogWarning("Cannot post empty week letter for {ChildName}", childName);
            return;
        }

        // Compute a hash of the week letter to avoid posting duplicates
        string hash = ComputeHash(weekLetter);

        // Check if we've already posted this week letter
        if (_postedWeekLetterHashes.Contains(hash))
        {
            _logger.LogInformation("Week letter for {ChildName} already posted, skipping", childName);
            return;
        }

        // Add the hash to our set
        _postedWeekLetterHashes.Add(hash);

        // Format the message with a title
        string message = $"*Ugeplan for {childName}: {weekLetterTitle}*\n\n{weekLetter}";

        try
        {
            _logger.LogInformation("Posting week letter for {ChildName}", childName);

            var payload = new
            {
                channel = _config.Slack.ChannelId,
                text = message,
                mrkdwn = true
            };

            var content = new StringContent(
                JsonConvert.SerializeObject(payload),
                Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            var response = await _httpClient.PostAsync("https://slack.com/api/chat.postMessage", content);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to post week letter: HTTP {StatusCode}", response.StatusCode);
                return;
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            var data = JObject.Parse(responseContent);

            if (data["ok"]?.Value<bool>() != true)
            {
                _logger.LogError("Failed to post week letter: {Error}", data["error"]?.ToString());
            }
            else
            {
                // Store the message ID to avoid processing it later
                string messageId = data["ts"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(messageId))
                {
                    _sentMessageIds.Add(messageId);
                    _messageTimestamps[messageId] = DateTime.UtcNow;
                    _logger.LogInformation("Stored week letter message ID: {MessageId}", messageId);
                }
                _logger.LogInformation("Week letter for {ChildName} posted successfully", childName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error posting week letter for {ChildName}", childName);
        }
    }

    private string ComputeHash(string input)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }

    private string GetDanishDayName(DayOfWeek dayOfWeek)
    {
        return dayOfWeek switch
        {
            DayOfWeek.Monday => "mandag",
            DayOfWeek.Tuesday => "tirsdag",
            DayOfWeek.Wednesday => "onsdag",
            DayOfWeek.Thursday => "torsdag",
            DayOfWeek.Friday => "fredag",
            DayOfWeek.Saturday => "lørdag",
            DayOfWeek.Sunday => "søndag",
            _ => "ukendt dag"
        };
    }

    private async Task HandleAulaQuestion(string text, bool isEnglish)
    {
        try
        {
            _logger.LogInformation("HandleAulaQuestion called with text: {Text}", text);

            // Get all children and their week letters
            var allChildren = await _agentService.GetAllChildrenAsync();
            if (!allChildren.Any())
            {
                string noChildrenMessage = isEnglish
                    ? "I don't have any children configured."
                    : "Jeg har ingen børn konfigureret.";

                await SendMessageInternal(noChildrenMessage);
                return;
            }

            // Collect week letters for all children
            var childrenWeekLetters = new Dictionary<string, JObject>();
            foreach (var child in allChildren)
            {
                var weekLetter = await _agentService.GetWeekLetterAsync(child, DateOnly.FromDateTime(DateTime.Today), true);
                if (weekLetter != null)
                {
                    childrenWeekLetters[child.FirstName] = weekLetter;
                }
            }

            if (!childrenWeekLetters.Any())
            {
                string noLettersMessage = isEnglish
                    ? "I don't have any week letters available at the moment."
                    : "Jeg har ingen ugebreve tilgængelige i øjeblikket.";

                await SendMessageInternal(noLettersMessage);
                return;
            }

            // Use a single context key for the channel
            string contextKey = $"slack-{_config.Slack.ChannelId}";

            // Add day context if needed
            string enhancedQuestion = text;
            if (text.ToLowerInvariant().Contains("i dag") || text.ToLowerInvariant().Contains("today"))
            {
                string dayOfWeek = isEnglish ? DateTime.Now.DayOfWeek.ToString() : GetDanishDayName(DateTime.Now.DayOfWeek);
                enhancedQuestion = $"{text} (Today is {dayOfWeek})";
            }
            else if (text.ToLowerInvariant().Contains("i morgen") || text.ToLowerInvariant().Contains("tomorrow"))
            {
                string dayOfWeek = isEnglish ? DateTime.Now.AddDays(1).DayOfWeek.ToString() : GetDanishDayName(DateTime.Now.AddDays(1).DayOfWeek);
                enhancedQuestion = $"{text} (Tomorrow is {dayOfWeek})";
            }

            // Use the new combined method
            string answer = await _agentService.AskQuestionAboutChildrenAsync(childrenWeekLetters, enhancedQuestion, contextKey, ChatInterface.Slack);

            await SendMessageInternal(answer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling Aula question");
            string errorMessage = isEnglish
                ? "Sorry, I couldn't process your question at the moment."
                : "Beklager, jeg kunne ikke behandle dit spørgsmål i øjeblikket.";

            await SendMessageInternal(errorMessage);
        }
    }

    private async Task HandleAllChildrenQuestion(string text, bool isEnglish)
    {
        try
        {
            // Get all children using the AgentService
            var allChildren = await _agentService.GetAllChildrenAsync();
            if (!allChildren.Any())
            {
                string noChildrenMessage = isEnglish
                    ? "I don't have any children configured."
                    : "Jeg har ingen børn konfigureret.";

                await SendMessageInternal(noChildrenMessage);
                return;
            }

            // Create a response builder
            var responseBuilder = new StringBuilder();

            // Add a header
            responseBuilder.AppendLine(isEnglish
                ? "Here's information for all children:"
                : "Her er information for alle børn:");

            // User's original question
            string userQuestion = text.Trim();

            foreach (var child in allChildren)
            {

                // Get the week letter for the child
                var weekLetter = await _agentService.GetWeekLetterAsync(child, DateOnly.FromDateTime(DateTime.Today), true);
                if (weekLetter == null)
                {
                    string noLetterMessage = isEnglish
                            ? $"- {child.FirstName}: No week letter available."
                            : $"- {child.FirstName}: Intet ugebrev tilgængeligt.";

                    responseBuilder.AppendLine(noLetterMessage);
                    continue;
                }

                // Create a context key for all-children query
                string contextKey = $"slack-{_config.Slack.ChannelId}-all-{child.FirstName.ToLowerInvariant()}";

                // Formulate a brief question for this child
                string question = $"{userQuestion} (About {child.FirstName}. Give a brief answer.)";

                // Ask OpenAI about the child's activities
                string answer = await _agentService.AskQuestionAboutWeekLetterAsync(child, DateOnly.FromDateTime(DateTime.Today), question, contextKey, ChatInterface.Slack);

                // Format the answer
                responseBuilder.AppendLine($"- {child.FirstName}: {answer.Trim()}");
            }

            await SendMessageInternal(responseBuilder.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling multi-child question");
            string errorMessage = isEnglish
                ? "Sorry, I couldn't retrieve information about the children's activities at the moment."
                : "Beklager, jeg kunne ikke hente information om børnenes aktiviteter i øjeblikket.";

            await SendMessageInternal(errorMessage);
        }
    }

    private void CleanupOldMessageIds(object? state)
    {
        try
        {
            // Keep message IDs for 24 hours to be safe
            var cutoff = DateTime.UtcNow.AddHours(-24);
            int removedCount = 0;

            // Find message IDs older than the cutoff
            var oldMessageIds = _messageTimestamps
                .Where(kvp => kvp.Value < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();

            // Remove them from both collections
            foreach (var messageId in oldMessageIds)
            {
                _sentMessageIds.Remove(messageId);
                _messageTimestamps.Remove(messageId);
                removedCount++;
            }

            // Also clean up the week letter hashes if there are too many
            if (_postedWeekLetterHashes.Count > 100)
            {
                // Since we can't easily determine which are oldest in a HashSet,
                // we'll just clear it if it gets too large
                _postedWeekLetterHashes.Clear();
                _logger.LogInformation("Cleared week letter hash cache");
            }

            if (removedCount > 0)
            {
                _logger.LogInformation("Cleaned up {Count} old message IDs. Remaining: {Remaining}",
                    removedCount, _sentMessageIds.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up old message IDs");
        }
    }

    // Helper method to detect if a question contains relative time references
    private bool ContainsRelativeTimeReference(string text)
    {
        string lowerText = text.ToLowerInvariant();

        // Check for common relative time words in Danish and English
        string[] relativeTimeWords = new[]
        {
            "tomorrow", "yesterday", "today", "next week", "last week", "tonight", "this morning",
            "i morgen", "i går", "i dag", "næste uge", "sidste uge", "i aften", "i morges"
        };

        return relativeTimeWords.Any(word => lowerText.Contains(word));
    }

    private async Task<bool> TryHandleHelpCommand(string text, bool isEnglish)
    {
        var helpPatterns = new[]
        {
            @"^(help|--help|\?|commands)$",
            @"^(hjælp|kommandoer)$"
        };

        foreach (var pattern in helpPatterns)
        {
            if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase))
            {
                string helpMessage = isEnglish ? GetEnglishHelpMessage() : GetDanishHelpMessage();
                await SendMessageInternal(helpMessage);
                return true;
            }
        }

        return false;
    }

    private string GetEnglishHelpMessage()
    {
        return """
📚 *AulaBot Commands & Usage*

*🤖 Interactive Questions:*
Ask me anything about your children's school activities in natural language:
• "What does Søren have today?"
• "Does Hans have homework tomorrow?"
• "What activities are planned this week?"

*⏰ Reminder Commands:*
• `remind me tomorrow at 8:00 that Hans has Haver til maver`
• `remind me 25/12 at 7:30 that Christmas breakfast`
• `list reminders` - Show all reminders
• `delete reminder 1` - Delete reminder with ID 1

*📅 Automatic Features:*
• Weekly letters posted every Sunday at 16:00
• Morning reminders sent when scheduled
• Retry logic for missing content

*💬 Language Support:*
Ask questions in English or Danish - I'll respond in the same language!

*ℹ️ Tips:*
• Use "today", "tomorrow", or specific dates
• Mention child names for targeted questions
• Follow-up questions maintain context for 10 minutes
""";
    }

    private string GetDanishHelpMessage()
    {
        return """
📚 *AulaBot Kommandoer & Brug*

*🤖 Interaktive Spørgsmål:*
Spørg mig om hvad som helst vedrørende dine børns skoleaktiviteter på naturligt sprog:
• "Hvad skal Søren i dag?"
• "Har Hans lektier i morgen?"
• "Hvilke aktiviteter er planlagt denne uge?"

*⏰ Påmindelseskommandoer:*
• `husk mig i morgen kl 8:00 at Hans har Haver til maver`
• `husk mig 25/12 kl 7:30 at julefrokost`
• `vis påmindelser` - Vis alle påmindelser
• `slet påmindelse 1` - Slet påmindelse med ID 1

*📅 Automatiske Funktioner:*
• Ugebreve postes hver søndag kl. 16:00
• Morgenpåmindelser sendes når planlagt
• Genforøgelseslogik for manglende indhold

*💬 Sprogunderstøttelse:*
Stil spørgsmål på engelsk eller dansk - jeg svarer på samme sprog!

*ℹ️ Tips:*
• Brug "i dag", "i morgen", eller specifikke datoer
• Nævn børnenes navne for målrettede spørgsmål
• Opfølgningsspørgsmål bevarer kontekst i 10 minutter
""";
    }

    private async Task<bool> TryHandleReminderCommand(string text, bool isEnglish)
    {
        text = text.Trim();

        // Check for various reminder command patterns
        if (await TryHandleAddReminder(text, isEnglish)) return true;
        if (await TryHandleListReminders(text, isEnglish)) return true;
        if (await TryHandleDeleteReminder(text, isEnglish)) return true;

        return false;
    }

    private async Task<bool> TryHandleAddReminder(string text, bool isEnglish)
    {
        // Patterns: "remind me tomorrow at 8:00 that Hans has Haver til maver"
        //           "husk mig i morgen kl 8:00 at Hans har Haver til maver"

        var reminderPatterns = new[]
        {
            @"remind me (tomorrow|today|\d{4}-\d{2}-\d{2}|\d{1,2}\/\d{1,2}) at (\d{1,2}:\d{2}) that (.+)",
            @"husk mig (i morgen|i dag|\d{4}-\d{2}-\d{2}|\d{1,2}\/\d{1,2}) kl (\d{1,2}:\d{2}) at (.+)"
        };

        foreach (var pattern in reminderPatterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                try
                {
                    var dateStr = match.Groups[1].Value.ToLowerInvariant();
                    var timeStr = match.Groups[2].Value;
                    var reminderText = match.Groups[3].Value;

                    // Parse date
                    DateOnly date;
                    if (dateStr == "tomorrow" || dateStr == "i morgen")
                    {
                        date = DateOnly.FromDateTime(DateTime.Today.AddDays(1));
                    }
                    else if (dateStr == "today" || dateStr == "i dag")
                    {
                        date = DateOnly.FromDateTime(DateTime.Today);
                    }
                    else if (DateOnly.TryParse(dateStr, out var parsedDate))
                    {
                        date = parsedDate;
                    }
                    else
                    {
                        // Try parsing DD/MM format
                        var dateParts = dateStr.Split('/');
                        if (dateParts.Length == 2 &&
                            int.TryParse(dateParts[0], out var day) &&
                            int.TryParse(dateParts[1], out var month))
                        {
                            var year = DateTime.Now.Year;
                            if (month < DateTime.Now.Month || (month == DateTime.Now.Month && day < DateTime.Now.Day))
                            {
                                year++; // Next year if date has passed
                            }
                            date = new DateOnly(year, month, day);
                        }
                        else
                        {
                            throw new FormatException("Invalid date format");
                        }
                    }

                    // Parse time
                    if (!TimeOnly.TryParse(timeStr, out var time))
                    {
                        throw new FormatException("Invalid time format");
                    }

                    // Extract child name if mentioned
                    string? childName = null;
                    foreach (var child in _childrenByName.Values)
                    {
                        string firstName = child.FirstName.Split(' ')[0];
                        if (reminderText.Contains(firstName, StringComparison.OrdinalIgnoreCase))
                        {
                            childName = child.FirstName;
                            break;
                        }
                    }

                    // Add reminder to database
                    var reminderId = await _supabaseService.AddReminderAsync(reminderText, date, time, childName);

                    string successMessage = isEnglish
                        ? $"✅ Reminder added (ID: {reminderId}): {reminderText} on {date:yyyy-MM-dd} at {time:HH:mm}"
                        : $"✅ Påmindelse tilføjet (ID: {reminderId}): {reminderText} den {date:yyyy-MM-dd} kl. {time:HH:mm}";

                    await SendMessageInternal(successMessage);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error adding reminder: {Text}", text);
                    string errorMessage = isEnglish
                        ? "❌ Error adding reminder. Please check the date and time format."
                        : "❌ Fejl ved tilføjelse af påmindelse. Tjek venligst dato og tidsformat.";

                    await SendMessageInternal(errorMessage);
                    return true;
                }
            }
        }

        return false;
    }

    private async Task<bool> TryHandleListReminders(string text, bool isEnglish)
    {
        var listPatterns = new[]
        {
            @"^(list reminders|show reminders)$",
            @"^(vis påmindelser|liste påmindelser)$"
        };

        foreach (var pattern in listPatterns)
        {
            if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase))
            {
                try
                {
                    var reminders = await _supabaseService.GetAllRemindersAsync();

                    if (!reminders.Any())
                    {
                        string noRemindersMessage = isEnglish
                            ? "📝 No reminders found."
                            : "📝 Ingen påmindelser fundet.";

                        await SendMessageInternal(noRemindersMessage);
                        return true;
                    }

                    var messageBuilder = new StringBuilder();
                    messageBuilder.AppendLine(isEnglish ? "📝 *Current Reminders:*" : "📝 *Nuværende påmindelser:*");

                    foreach (var reminder in reminders.OrderBy(r => r.RemindDate).ThenBy(r => r.RemindTime))
                    {
                        string status = reminder.IsSent ? "✅" : "⏰";
                        string childInfo = !string.IsNullOrEmpty(reminder.ChildName) ? $" ({reminder.ChildName})" : "";

                        messageBuilder.AppendLine($"{status} *ID {reminder.Id}*: {reminder.Text}{childInfo}");
                        messageBuilder.AppendLine($"   📅 {reminder.RemindDate:yyyy-MM-dd} ⏰ {reminder.RemindTime:HH:mm}");
                    }

                    await SendMessageInternal(messageBuilder.ToString());
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error listing reminders");
                    string errorMessage = isEnglish
                        ? "❌ Error retrieving reminders."
                        : "❌ Fejl ved hentning af påmindelser.";

                    await SendMessageInternal(errorMessage);
                    return true;
                }
            }
        }

        return false;
    }

    private async Task<bool> TryHandleDeleteReminder(string text, bool isEnglish)
    {
        var deletePatterns = new[]
        {
            @"^delete reminder (\d+)$",
            @"^slet påmindelse (\d+)$"
        };

        foreach (var pattern in deletePatterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                try
                {
                    var reminderId = int.Parse(match.Groups[1].Value);

                    await _supabaseService.DeleteReminderAsync(reminderId);

                    string successMessage = isEnglish
                        ? $"✅ Reminder {reminderId} deleted."
                        : $"✅ Påmindelse {reminderId} slettet.";

                    await SendMessageInternal(successMessage);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error deleting reminder: {Text}", text);
                    string errorMessage = isEnglish
                        ? "❌ Error deleting reminder. Please check the reminder ID."
                        : "❌ Fejl ved sletning af påmindelse. Tjek venligst påmindelse ID.";

                    await SendMessageInternal(errorMessage);
                    return true;
                }
            }
        }

        return false;
    }
}