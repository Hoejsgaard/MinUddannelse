using System;
using MinUddannelse.Content.WeekLetters;
using System.Linq;
using System.Threading.Tasks;
using MinUddannelse.Configuration;
using Microsoft.Extensions.Logging;

namespace MinUddannelse.AI.Services;

public class OpenAiService : IOpenAiService
{
    private readonly IWeekLetterAiService _openAiService;
    private readonly IWeekLetterService _weekLetterService;
    private readonly ILogger _logger;

    public OpenAiService(
        IWeekLetterAiService openAiService,
        IWeekLetterService weekLetterService,
        ILoggerFactory loggerFactory)
    {
        _openAiService = openAiService ?? throw new ArgumentNullException(nameof(openAiService));
        _weekLetterService = weekLetterService ?? throw new ArgumentNullException(nameof(weekLetterService));
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger<OpenAiService>();
    }

    public async Task<string?> GetResponseAsync(Child child, string query)
    {
        if (child == null) throw new ArgumentNullException(nameof(child));

        _logger.LogInformation("Getting AI response for child {ChildName}: {Query}", child.FirstName, query);

        // Let the AI intent analysis system handle all routing decisions
        var contextualQuery = $"[Context: Child {child.FirstName}] {query}";
        return await _openAiService.ProcessQueryWithToolsAsync(contextualQuery,
            $"child_{child.FirstName}",
            ChatInterface.Slack);
    }


    public async Task<string?> GetResponseWithContextAsync(Child child, string query, string conversationId)
    {
        if (child == null) throw new ArgumentNullException(nameof(child));

        _logger.LogInformation("Getting AI response with context for child {ChildName}, conversation {ConversationId}",
            child.FirstName, conversationId);

        var childConversationId = $"{child.FirstName}_{conversationId}";

        // Include child context so reminder extraction knows which child the reminder is for
        var contextualQuery = $"[Context: Child {child.FirstName}] {query}";
        return await _openAiService.ProcessQueryWithToolsAsync(contextualQuery,
            childConversationId,
            ChatInterface.Slack);
    }

    public async Task ClearConversationHistoryAsync(Child child, string conversationId)
    {
        if (child == null) throw new ArgumentNullException(nameof(child));

        _logger.LogInformation("Clearing conversation history for child {ChildName}, conversation {ConversationId}",
            child.FirstName, conversationId);

        var childConversationId = $"{child.FirstName}_{conversationId}";

        _openAiService.ClearConversationHistory(childConversationId);
        await Task.CompletedTask;
    }

    public async Task<CompletionResult> CreateCompletionAsync(string prompt, string model)
    {
        try
        {
            var response = await _openAiService.ProcessDirectQueryAsync(prompt, ChatInterface.Slack);

            if (string.IsNullOrWhiteSpace(response) || response == "FALLBACK_TO_EXISTING_SYSTEM")
            {
                return new CompletionResult
                {
                    IsSuccess = false,
                    ErrorMessage = "Failed to get valid response from OpenAI service"
                };
            }

            return new CompletionResult
            {
                IsSuccess = true,
                Content = response
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating completion with prompt length {PromptLength}", prompt?.Length ?? 0);
            return new CompletionResult
            {
                IsSuccess = false,
                ErrorMessage = ex.Message
            };
        }
    }
}
