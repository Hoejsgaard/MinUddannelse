using Microsoft.Extensions.Logging;
using MinUddannelse.AI.Prompts;
using MinUddannelse.Models;
using MinUddannelse.Repositories.DTOs;
using Newtonsoft.Json.Linq;
using OpenAI.ObjectModels;
using OpenAI.ObjectModels.RequestModels;
using OpenAI.ObjectModels.ResponseModels;
using OpenAI.Managers;
using OpenAI;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using MinUddannelse.AI.Services;
using MinUddannelse.Configuration;
using MinUddannelse.Content.WeekLetters;

namespace MinUddannelse.AI.Services;

public class WeekLetterAiService : IWeekLetterAiService
{
    private readonly OpenAIService _openAiClient;
    private readonly ILogger _logger;
    private readonly IAiToolsManager _aiToolsManager;
    private readonly IConversationManager _conversationManager;
    private readonly IPromptBuilder _promptBuilder;
    private readonly string _aiModel;

    private const int MaxConversationHistoryWeekLetter = 20;
    private const int ConversationTrimAmount = 4;
    private const string FallbackToExistingSystem = "FALLBACK_TO_EXISTING_SYSTEM";

    public WeekLetterAiService(string apiKey, ILoggerFactory loggerFactory, IAiToolsManager aiToolsManager, IConversationManager conversationManager, IPromptBuilder promptBuilder, string? model = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key cannot be null or empty", nameof(apiKey));
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(aiToolsManager);
        ArgumentNullException.ThrowIfNull(conversationManager);
        ArgumentNullException.ThrowIfNull(promptBuilder);

        _aiModel = model ?? "gpt-3.5-turbo";
        _openAiClient = new OpenAIService(new OpenAiOptions()
        {
            ApiKey = apiKey
        });
        _logger = loggerFactory.CreateLogger(nameof(WeekLetterAiService));
        _aiToolsManager = aiToolsManager;
        _conversationManager = conversationManager;
        _promptBuilder = promptBuilder;
    }

    private static (string childName, string className, string weekNumber) ExtractWeekLetterMetadata(JObject weekLetter)
    {
        string childName = "unknown";
        string className = "unknown";
        string weekNumber = "unknown";

        if (weekLetter["ugebreve"] != null && weekLetter["ugebreve"] is JArray ugebreve && ugebreve.Count > 0)
        {
            className = ugebreve[0]?["klasseNavn"]?.ToString() ?? "unknown";
            weekNumber = ugebreve[0]?["uge"]?.ToString() ?? "unknown";
        }

        if (weekLetter["child"] != null) childName = weekLetter["child"]?.ToString() ?? "unknown";

        return (childName, className, weekNumber);
    }

    public async Task<string> SummarizeWeekLetterAsync(JObject weekLetter, ChatInterface chatInterface = ChatInterface.Slack)
    {
        _logger.LogInformation("Summarizing week letter for {ChatInterface}", chatInterface);

        var weekLetterContent = ExtractWeekLetterContent(weekLetter);
        var (childName, className, weekNumber) = ExtractWeekLetterMetadata(weekLetter);

        var messages = _promptBuilder.CreateSummarizationMessages(childName, className, weekNumber, weekLetterContent, chatInterface);

        var chatRequest = new ChatCompletionCreateRequest
        {
            Messages = messages,
            Model = _aiModel,
            Temperature = 0.7f
        };

        var response = await _openAiClient.ChatCompletion.CreateCompletion(chatRequest);

        if (response.Successful)
        {
            return response.Choices.First().Message.Content ?? "No response content received.";
        }
        else
        {
            _logger.LogError("Error calling OpenAI API: {Error}", response.Error?.Message);
            return "Sorry, I couldn't summarize the week letter at this time.";
        }
    }

    public async Task<string> AskQuestionAboutWeekLetterAsync(JObject weekLetter, string question, string childName, ChatInterface chatInterface = ChatInterface.Slack)
    {
        return await AskQuestionAboutWeekLetterAsync(weekLetter, question, childName, null, chatInterface);
    }

    public async Task<string> AskQuestionAboutWeekLetterAsync(JObject weekLetter, string question, string childName, string? contextKey, ChatInterface chatInterface = ChatInterface.Slack)
    {
        var weekLetterContent = ExtractWeekLetterContent(weekLetter);
        contextKey = _conversationManager.EnsureContextKey(contextKey, childName);

        _conversationManager.EnsureConversationHistory(contextKey, childName, weekLetterContent, chatInterface);
        _conversationManager.AddUserQuestionToHistory(contextKey, question);
        _conversationManager.TrimConversationHistoryIfNeeded(contextKey);
        LogConversationHistoryStructure(contextKey);

        return await SendChatRequestAndGetResponse(contextKey);
    }


    private void LogConversationHistoryStructure(string contextKey)
    {
        var messages = _conversationManager.GetConversationHistory(contextKey);
        _logger.LogInformation("🔎 TRACE: Conversation history structure:");
        for (int i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            _logger.LogInformation("🔎 TRACE: Message {Index}: Role={Role}, Content Length={Length}",
                i, message.Role, message.Content?.Length ?? 0);

            if (message.Content != null && message.Content.Length > 0)
            {
                var preview = message.Content.Length > 100 ? message.Content.Substring(0, 100) + "..." : message.Content;
                _logger.LogInformation("🔎 TRACE: Message {Index} Preview: {Preview}", i, preview);
            }
        }
    }

    private async Task<string> SendChatRequestAndGetResponse(string contextKey)
    {
        var messages = _conversationManager.GetConversationHistory(contextKey);
        var chatRequest = new ChatCompletionCreateRequest
        {
            Messages = messages,
            Model = _aiModel,
            Temperature = 0.7f
        };

        var response = await _openAiClient.ChatCompletion.CreateCompletion(chatRequest);

        if (response.Successful)
        {
            var reply = response.Choices.First().Message.Content ?? "No response content received.";
            _conversationManager.AddAssistantResponseToHistory(contextKey, reply);
            return reply;
        }
        else
        {
            _logger.LogError("Error calling OpenAI API: {Error}", response.Error?.Message);
            return "Sorry, I couldn't answer your question at this time.";
        }
    }

    public async Task<JObject> ExtractKeyInformationAsync(JObject weekLetter, ChatInterface chatInterface = ChatInterface.Slack)
    {
        _logger.LogInformation("Extracting key information from week letter for {ChatInterface}", chatInterface);

        var weekLetterContent = ExtractWeekLetterContent(weekLetter);
        var (childName, className, weekNumber) = ExtractWeekLetterMetadata(weekLetter);

        var messages = _promptBuilder.CreateKeyInformationExtractionMessages(childName, className, weekNumber, weekLetterContent);

        var chatRequest = new ChatCompletionCreateRequest
        {
            Messages = messages,
            Model = _aiModel,
            Temperature = 0.3f
        };

        var response = await _openAiClient.ChatCompletion.CreateCompletion(chatRequest);

        if (response.Successful)
        {
            var jsonResponse = response.Choices.First().Message.Content;
            try
            {
                if (string.IsNullOrEmpty(jsonResponse))
                {
                    return new JObject();
                }
                return JObject.Parse(jsonResponse);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing JSON response from OpenAI");
                return new JObject();
            }
        }
        else
        {
            _logger.LogError("Error calling OpenAI API: {Error}", response.Error?.Message);
            return new JObject();
        }
    }

    private string ExtractWeekLetterContent(JObject weekLetter)
    {
        return WeekLetterContentExtractor.ExtractContent(weekLetter, _logger);
    }

    public async Task<string> AskQuestionAboutChildrenAsync(Dictionary<string, JObject> childrenWeekLetters, string question, string? contextKey, ChatInterface chatInterface = ChatInterface.Slack)
    {
        _logger.LogInformation("Asking question about multiple children: {Question} for {ChatInterface}", question, chatInterface);

        var childrenContent = new Dictionary<string, string>();
        foreach (var (childName, weekLetter) in childrenWeekLetters)
        {
            childrenContent[childName] = ExtractWeekLetterContent(weekLetter);
        }

        var contextKeyToUse = contextKey ?? "combined-children";

        var baseMessages = _promptBuilder.CreateMultiChildMessages(childrenContent, chatInterface);
        var existingHistory = _conversationManager.GetConversationHistory(contextKeyToUse);

        var messages = new List<ChatMessage>(baseMessages);
        messages.AddRange(existingHistory);
        messages.Add(ChatMessage.FromUser(question));

        var chatRequest = new ChatCompletionCreateRequest
        {
            Messages = messages,
            Model = _aiModel,
            Temperature = 0.3f
        };

        var response = await _openAiClient.ChatCompletion.CreateCompletion(chatRequest);

        if (response.Successful)
        {
            var answer = response.Choices.First().Message.Content ?? "I couldn't generate a response.";

            _conversationManager.AddUserQuestionToHistory(contextKeyToUse, question);
            _conversationManager.AddAssistantResponseToHistory(contextKeyToUse, answer);
            _conversationManager.TrimMultiChildConversationIfNeeded(contextKeyToUse);

            return answer;
        }
        else
        {
            _logger.LogError("Error calling OpenAI API: {Error}", response.Error?.Message);
            return "I'm sorry, I couldn't process your question at the moment.";
        }
    }

    public async Task<string> ProcessDirectQueryAsync(string query, ChatInterface chatInterface = ChatInterface.Slack)
    {
        try
        {
            _logger.LogInformation("Processing direct query: {Query}", query);

            var messages = new List<ChatMessage>
            {
                ChatMessage.FromUser(query)
            };

            var chatRequest = new ChatCompletionCreateRequest
            {
                Messages = messages,
                Model = _aiModel,
                Temperature = 0.7f
            };

            var response = await _openAiClient.ChatCompletion.CreateCompletion(chatRequest);

            if (response.Successful)
            {
                var reply = response.Choices.First().Message.Content ?? "No response content received.";
                _logger.LogInformation("Direct query processed successfully");
                return reply;
            }
            else
            {
                _logger.LogError("Error calling OpenAI API: {Error}", response.Error?.Message);
                return "Sorry, I couldn't answer your question at this time.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing direct query");
            return "❌ I encountered an error processing your request. Please try again.";
        }
    }

    public void ClearConversationHistory(string? contextKey = null)
    {
        _conversationManager.ClearConversationHistory(contextKey);
    }

    public async Task<string> ProcessQueryWithToolsAsync(string query, string contextKey, ChatInterface chatInterface = ChatInterface.Slack)
    {
        try
        {
            _logger.LogInformation("Processing query with intelligent tool selection: {Query}", query);

            var analysisResponse = await AnalyzeUserIntentAsync(query, chatInterface);

            _logger.LogInformation("Intent analysis result: {Analysis}", analysisResponse);

            if (analysisResponse.Contains("TOOL_CALL:"))
            {
                return await HandleToolBasedQuery(query, analysisResponse, contextKey, chatInterface);
            }
            else
            {
                return await HandleRegularAulaQuery(query, contextKey, chatInterface);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing query with tools");
            return "❌ I encountered an error processing your request. Please try again.";
        }
    }

    private async Task<string> AnalyzeUserIntentAsync(string query, ChatInterface chatInterface)
    {
        try
        {
            _logger.LogInformation("Starting intent analysis for query: {Query}", query);

            var analysisPrompt = IntentAnalysisPrompts.GetFormattedPrompt(query);

            var chatRequest = new ChatCompletionCreateRequest
            {
                Model = _aiModel,
                Messages = new List<ChatMessage>
            {
                ChatMessage.FromSystem(analysisPrompt)
            },
                Temperature = 0.1f
            };

            _logger.LogInformation("Sending intent analysis request to OpenAI");
            var response = await _openAiClient.ChatCompletion.CreateCompletion(chatRequest);

            if (response.Successful)
            {
                var intentAnalysis = response.Choices.First().Message.Content ?? "INFORMATION_QUERY";
                _logger.LogInformation("Intent analysis completed successfully: {Result}", intentAnalysis);
                return intentAnalysis;
            }
            else
            {
                _logger.LogError("Error analyzing user intent: {Error}", response.Error?.Message);
                return "INFORMATION_QUERY"; // Fallback to regular query
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during intent analysis");
            return "INFORMATION_QUERY"; // Fallback to regular query
        }
    }

    private async Task<string> HandleToolBasedQuery(string query, string analysis, string contextKey, ChatInterface chatInterface)
    {
        try
        {
            var toolType = analysis.Replace("TOOL_CALL:", "").Trim();

            _logger.LogInformation("Handling tool-based query with tool: {ToolType}", toolType);

            return toolType switch
            {
                "CREATE_REMINDER" => await HandleCreateReminderQuery(query),
                "LIST_REMINDERS" => await HandleListRemindersQuery(query),
                "DELETE_REMINDER" => await HandleDeleteReminderQuery(query),
                "GET_CURRENT_TIME" => await HandleGetCurrentTimeQuery(query),
                "HELP" => await HandleHelpQuery(query),
                _ => await HandleRegularAulaQuery(query, contextKey, chatInterface)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling tool-based query");
            return await GenerateLanguageAwareErrorMessage(query, "action_failed");
        }
    }

    private async Task<string> HandleListRemindersQuery(string query)
    {
        var reminderList = await _aiToolsManager.ListRemindersAsync();

        // If there's an error from AiToolsManager, return it
        if (reminderList.StartsWith('❌') || reminderList.StartsWith("No active reminders"))
        {
            return await GenerateLanguageAwareResponse(query, reminderList, "reminder_list");
        }

        // Generate language-appropriate response
        return await GenerateLanguageAwareResponse(query, reminderList, "reminder_list");
    }

    private async Task<string> HandleGetCurrentTimeQuery(string query)
    {
        var currentTime = _aiToolsManager.GetCurrentDateTime();
        return await GenerateLanguageAwareResponse(query, currentTime, "current_time");
    }

    private async Task<string> HandleHelpQuery(string query)
    {
        var helpContent = _aiToolsManager.GetHelp();
        return await GenerateLanguageAwareResponse(query, helpContent, "help");
    }

    private async Task<string> GenerateLanguageAwareResponse(string originalQuery, string content, string responseType)
    {
        var responsePrompt = $@"The user asked: ""{originalQuery}""

I have this information for them:
{content}

Generate a response in the SAME LANGUAGE as the user's original request. The response type is: {responseType}

Examples:
- If user wrote in Danish: respond in Danish
- If user wrote in English: respond in English
- Keep the same information but present it in the user's language
- Maintain the same tone and style

Generate the response:";

        try
        {
            var chatRequest = new ChatCompletionCreateRequest
            {
                Model = _aiModel,
                Messages = new List<ChatMessage>
                {
                    ChatMessage.FromSystem(responsePrompt)
                },
                Temperature = 0.3f
            };

            var response = await _openAiClient.ChatCompletion.CreateCompletion(chatRequest);

            if (response.Successful)
            {
                return response.Choices.First().Message.Content ?? content;
            }
            else
            {
                _logger.LogError("Error generating language-aware response: {Error}", response.Error?.Message);
                return content;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception generating language-aware response");
            return content;
        }
    }

    private async Task<string> HandleCreateReminderQuery(string query)
    {
        var extractionPrompt = ReminderExtractionPrompts.GetExtractionPrompt(query, DateTime.Now);

        var chatRequest = new ChatCompletionCreateRequest
        {
            Model = _aiModel,
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromSystem(extractionPrompt)
            },
            Temperature = 0.1f
        };

        try
        {
            _logger.LogInformation("Sending reminder extraction request to OpenAI");
            var response = await _openAiClient.ChatCompletion.CreateCompletion(chatRequest);

            if (response.Successful)
            {
                var content = response.Choices.First().Message.Content ?? "";
                _logger.LogInformation("Reminder extraction completed successfully");

                var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var description = ExtractValue(lines, "DESCRIPTION") ?? "Reminder";
                var dateTimeStr = ExtractValue(lines, "DATETIME") ?? DateTime.Now.AddHours(1).ToString("yyyy-MM-dd HH:mm");
                var childName = ExtractValue(lines, "CHILD");
                var isRecurringStr = ExtractValue(lines, "IS_RECURRING") ?? "false";
                var recurrenceType = ExtractValue(lines, "RECURRENCE_TYPE") ?? "NONE";
                var dayOfWeekStr = ExtractValue(lines, "DAY_OF_WEEK") ?? "NONE";

                if (!DateTime.TryParseExact(dateTimeStr, "yyyy-MM-dd HH:mm", null, System.Globalization.DateTimeStyles.None, out _))
                {
                    _logger.LogWarning("Invalid datetime format from AI: {DateTime}", dateTimeStr);
                    dateTimeStr = DateTime.Now.AddHours(1).ToString("yyyy-MM-dd HH:mm");
                }

                if (childName == "NONE") childName = null;

                // Check if this is a recurring reminder
                var isRecurring = bool.TryParse(isRecurringStr, out var recurring) && recurring;

                // Create the reminder using AiToolsManager (for database operations)
                string creationResult;
                if (isRecurring && recurrenceType != "NONE" && dayOfWeekStr != "NONE" && int.TryParse(dayOfWeekStr, out var dayOfWeek))
                {
                    creationResult = await _aiToolsManager.CreateRecurringReminderAsync(description, dateTimeStr, recurrenceType, dayOfWeek, childName);
                }
                else
                {
                    creationResult = await _aiToolsManager.CreateReminderAsync(description, dateTimeStr, childName);
                }

                // Check if reminder creation was successful
                if (creationResult.StartsWith('❌'))
                {
                    // Return error from AiToolsManager
                    return creationResult;
                }

                // Generate language-appropriate confirmation response
                var dayOfWeekForConfirmation = isRecurring && int.TryParse(dayOfWeekStr, out var parsedDayOfWeek) ? parsedDayOfWeek : 0;
                return await GenerateLanguageAwareConfirmation(query, description, dateTimeStr, childName, isRecurring, recurrenceType, dayOfWeekForConfirmation);
            }
            else
            {
                _logger.LogError("Error extracting reminder details: {Error}", response.Error?.Message);
                return await GenerateLanguageAwareErrorMessage(query, "extraction_failed");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during reminder extraction");
            return await GenerateLanguageAwareErrorMessage(query, "exception_occurred");
        }
    }

    private async Task<string> GenerateLanguageAwareConfirmation(string originalQuery, string description, string dateTime, string? childName, bool isRecurring, string recurrenceType, int dayOfWeek)
    {
        var confirmationPrompt = $@"The user made this request: ""{originalQuery}""

I have successfully created a reminder with these details:
- Description: {description}
- Date/Time: {dateTime}
- Child: {(childName ?? "any child")}
- Is Recurring: {isRecurring}
- Recurrence Type: {recurrenceType}
- Day of Week: {dayOfWeek}

Generate a brief, friendly confirmation message in the SAME LANGUAGE as the user's original request. Use an appropriate emoji (like ✅) and keep it concise. Match the tone and language of the original request.

Examples:
- If user wrote in Danish: respond in Danish
- If user wrote in English: respond in English
- Keep it brief and positive
- Include key details like what was created and when

Generate the confirmation message:";

        try
        {
            var chatRequest = new ChatCompletionCreateRequest
            {
                Model = _aiModel,
                Messages = new List<ChatMessage>
                {
                    ChatMessage.FromSystem(confirmationPrompt)
                },
                Temperature = 0.3f
            };

            var response = await _openAiClient.ChatCompletion.CreateCompletion(chatRequest);

            if (response.Successful)
            {
                return response.Choices.First().Message.Content ?? "✅ Reminder created successfully.";
            }
            else
            {
                _logger.LogError("Error generating confirmation message: {Error}", response.Error?.Message);
                return "✅ Reminder created successfully.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception generating confirmation message");
            return "✅ Reminder created successfully.";
        }
    }

    private async Task<string> GenerateLanguageAwareErrorMessage(string originalQuery, string errorType)
    {
        var errorPrompt = $@"The user made this request: ""{originalQuery}""

I encountered an error while processing their reminder request. The error type is: {errorType}

Generate a brief, helpful error message in the SAME LANGUAGE as the user's original request. Use an appropriate emoji (like ❌) and suggest what they could try instead.

Examples:
- If user wrote in Danish: respond in Danish
- If user wrote in English: respond in English
- Keep it brief and helpful
- Suggest they try again or be more specific

Generate the error message:";

        try
        {
            var chatRequest = new ChatCompletionCreateRequest
            {
                Model = _aiModel,
                Messages = new List<ChatMessage>
                {
                    ChatMessage.FromSystem(errorPrompt)
                },
                Temperature = 0.3f
            };

            var response = await _openAiClient.ChatCompletion.CreateCompletion(chatRequest);

            if (response.Successful)
            {
                return response.Choices.First().Message.Content ?? "❌ I couldn't understand the reminder details. Please try again with a clearer format.";
            }
            else
            {
                return "❌ I couldn't understand the reminder details. Please try again with a clearer format.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception generating error message");
            return "❌ I couldn't understand the reminder details. Please try again with a clearer format.";
        }
    }

    private async Task<string> HandleDeleteReminderQuery(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
        {
            if (int.TryParse(word, out var reminderNumber))
            {
                var deleteResult = await _aiToolsManager.DeleteReminderAsync(reminderNumber);

                // Check if deletion was successful
                if (deleteResult.StartsWith('❌'))
                {
                    // Return error from AiToolsManager
                    return deleteResult;
                }

                // Generate language-appropriate confirmation response
                return await GenerateLanguageAwareDeletionConfirmation(query, reminderNumber);
            }
        }

        return await GenerateLanguageAwareErrorMessage(query, "no_reminder_number");
    }

    private async Task<string> GenerateLanguageAwareDeletionConfirmation(string originalQuery, int reminderNumber)
    {
        var confirmationPrompt = $@"The user made this request: ""{originalQuery}""

I have successfully deleted reminder number {reminderNumber}.

Generate a brief, friendly confirmation message in the SAME LANGUAGE as the user's original request. Use an appropriate emoji (like ✅) and keep it concise. Match the tone and language of the original request.

Examples:
- If user wrote in Danish: respond in Danish
- If user wrote in English: respond in English
- Keep it brief and positive

Generate the confirmation message:";

        try
        {
            var chatRequest = new ChatCompletionCreateRequest
            {
                Model = _aiModel,
                Messages = new List<ChatMessage>
                {
                    ChatMessage.FromSystem(confirmationPrompt)
                },
                Temperature = 0.3f
            };

            var response = await _openAiClient.ChatCompletion.CreateCompletion(chatRequest);

            if (response.Successful)
            {
                return response.Choices.First().Message.Content ?? "✅ Reminder deleted successfully.";
            }
            else
            {
                _logger.LogError("Error generating deletion confirmation: {Error}", response.Error?.Message);
                return "✅ Reminder deleted successfully.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception generating deletion confirmation");
            return "✅ Reminder deleted successfully.";
        }
    }

    private async Task<string> HandleRegularAulaQuery(string query, string contextKey, ChatInterface chatInterface)
    {
        var lowerQuery = query.ToLowerInvariant();

        if (lowerQuery.Contains("activity") || lowerQuery.Contains("aktivitet"))
        {
            return await GenerateLanguageAwareHelpMessage(query, "activities");
        }

        if (lowerQuery.Contains("remind") || lowerQuery.Contains("mind"))
        {
            return await GenerateLanguageAwareHelpMessage(query, "reminders");
        }

        _logger.LogInformation("Delegating Aula query to existing system: {Query}", query);
        return FallbackToExistingSystem;
    }

    private async Task<string> GenerateLanguageAwareHelpMessage(string originalQuery, string helpType)
    {
        var helpPrompt = $@"The user asked: ""{originalQuery}""

They seem to need help with {helpType}. Generate a brief, helpful message in the SAME LANGUAGE as the user's original request that explains how to ask about {helpType}.

Examples:
- If user wrote in Danish: respond in Danish
- If user wrote in English: respond in English
- Keep it brief and helpful
- Provide example queries they could try

Generate the help message:";

        try
        {
            var chatRequest = new ChatCompletionCreateRequest
            {
                Model = _aiModel,
                Messages = new List<ChatMessage>
                {
                    ChatMessage.FromSystem(helpPrompt)
                },
                Temperature = 0.3f
            };

            var response = await _openAiClient.ChatCompletion.CreateCompletion(chatRequest);

            if (response.Successful)
            {
                return response.Choices.First().Message.Content ?? "I can help you with that. Please try asking in a different way.";
            }
            else
            {
                return "I can help you with that. Please try asking in a different way.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception generating help message");
            return "I can help you with that. Please try asking in a different way.";
        }
    }

    private static string? ExtractValue(string[] lines, string prefix)
    {
        var line = lines.FirstOrDefault(l => l.StartsWith($"{prefix}:"));
        return line?.Substring(prefix.Length + 1).Trim();
    }
}
