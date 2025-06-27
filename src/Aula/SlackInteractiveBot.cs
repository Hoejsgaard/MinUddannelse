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
    private bool _recentlyRespondedToGenericQuestion = false;
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

    public SlackInteractiveBot(
        IAgentService agentService,
        Config config,
        ILoggerFactory loggerFactory)
    {
        _agentService = agentService;
        _config = config;
        _logger = loggerFactory.CreateLogger<SlackInteractiveBot>();
        _httpClient = new HttpClient();
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
        await SendMessage($"Jeg er online og har ugeplan for {childrenList} for Uge {weekNumber}");
        
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
            
            _logger.LogInformation("Polling for messages since timestamp: {Timestamp}", adjustedTimestamp);
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
            
            // Debug the raw messages to understand what we're getting
            _logger.LogInformation("Raw messages received: {Count}", messages.Count);
            foreach (var msg in messages.Take(3))
            {
                _logger.LogInformation("Message: TS={Timestamp}, Type={Type}, SubType={SubType}, User={User}, BotId={BotId}",
                    msg["ts"], msg["type"], msg["subtype"], msg["user"], msg["bot_id"]);
            }
            
            // Get actual new messages (not from the bot)
            var userMessages = messages.Where(m => {
                // Skip messages we sent ourselves (by checking the ID)
                string messageId = m["ts"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(messageId) && _sentMessageIds.Contains(messageId))
                {
                    _logger.LogInformation("Skipping our own message with ID: {MessageId}", messageId);
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
        
        // Handle all Aula questions with a single method
        await HandleAulaQuestion(text, isEnglish);
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
            
            await SendMessage(notFoundMessage);
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
            
            await SendMessage(errorMessage);
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

    private async Task SendMessage(string text)
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
                await SendMessage("I need to be invited to this channel. Please use `/invite @YourBotName` in the channel.");
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
                
                await SendMessage(noChildrenMessage);
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
                
                await SendMessage(noLettersMessage);
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
            
            await SendMessage(answer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling Aula question");
            string errorMessage = isEnglish
                ? "Sorry, I couldn't process your question at the moment."
                : "Beklager, jeg kunne ikke behandle dit spørgsmål i øjeblikket.";
            
            await SendMessage(errorMessage);
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
                
                await SendMessage(noChildrenMessage);
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
            
            await SendMessage(responseBuilder.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling multi-child question");
            string errorMessage = isEnglish
                ? "Sorry, I couldn't retrieve information about the children's activities at the moment."
                : "Beklager, jeg kunne ikke hente information om børnenes aktiviteter i øjeblikket.";
            
            await SendMessage(errorMessage);
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
} 