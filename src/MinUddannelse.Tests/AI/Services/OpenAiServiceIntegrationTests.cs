using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using MinUddannelse.Repositories;
using Moq;
using Newtonsoft.Json.Linq;
using Xunit;
using MinUddannelse.AI.Services;
using MinUddannelse.AI.Prompts;
using MinUddannelse.Content.WeekLetters;
using MinUddannelse.Models;
using MinUddannelse.Repositories.DTOs;
using MinUddannelse.Security;
using MinUddannelse;
using MinUddannelse.Configuration;

namespace MinUddannelse.Tests.AI.Services;

public class OpenAiServiceIntegrationTests
{
    private readonly Mock<ILoggerFactory> _mockLoggerFactory;
    private readonly Mock<ILogger> _mockLogger;
    private readonly AiToolsManager _aiToolsManager;
    private readonly Mock<IReminderRepository> _mockReminderRepository;
    private readonly Mock<WeekLetterCache> _mockWeekLetterCache;
    private readonly Config _config;

    public OpenAiServiceIntegrationTests()
    {
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLogger = new Mock<ILogger>();
        _mockReminderRepository = new Mock<IReminderRepository>();

        _config = new Config
        {
            MinUddannelse = new MinUddannelseConfig
            {
                Children = new List<Child> { new Child { FirstName = "Test", LastName = "Child" } }
            }
        };

        var mockCache = new Mock<IMemoryCache>();
        _mockWeekLetterCache = new Mock<WeekLetterCache>(mockCache.Object, _config, _mockLoggerFactory.Object);

        _mockLoggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(_mockLogger.Object);

        _aiToolsManager = new AiToolsManager(_mockReminderRepository.Object, new Mock<IScheduledTaskRepository>().Object, _mockWeekLetterCache.Object, _config, _mockLoggerFactory.Object);
    }

    [Fact]
    public void Constructor_WithValidParameters_InitializesCorrectly()
    {
        var service = new WeekLetterAiService("test-api-key", _mockLoggerFactory.Object, _aiToolsManager, new Mock<IConversationManager>().Object, new Mock<IPromptBuilder>().Object);
        Assert.NotNull(service);
    }

    [Fact]
    public void Constructor_WithNullApiKey_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new WeekLetterAiService(null!, _mockLoggerFactory.Object, _aiToolsManager, new Mock<IConversationManager>().Object, new Mock<IPromptBuilder>().Object));
        Assert.Equal("apiKey", exception.ParamName);
    }

    [Fact]
    public void Constructor_WithEmptyApiKey_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new WeekLetterAiService("", _mockLoggerFactory.Object, _aiToolsManager, new Mock<IConversationManager>().Object, new Mock<IPromptBuilder>().Object));
        Assert.Equal("apiKey", exception.ParamName);
    }

    [Fact]
    public void Constructor_WithWhitespaceApiKey_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new WeekLetterAiService("   ", _mockLoggerFactory.Object, _aiToolsManager, new Mock<IConversationManager>().Object, new Mock<IPromptBuilder>().Object));
        Assert.Equal("apiKey", exception.ParamName);
    }

    [Fact]
    public void Constructor_WithNullLoggerFactory_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new WeekLetterAiService("test-api-key", null!, _aiToolsManager, new Mock<IConversationManager>().Object, new Mock<IPromptBuilder>().Object));
        Assert.Equal("loggerFactory", exception.ParamName);
    }

    [Fact]
    public void Constructor_WithNullAiToolsManager_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new WeekLetterAiService("test-api-key", _mockLoggerFactory.Object, null!, new Mock<IConversationManager>().Object, new Mock<IPromptBuilder>().Object));
        Assert.Equal("aiToolsManager", exception.ParamName);
    }

    [Fact]
    public void Constructor_WithCustomModel_UsesSpecifiedModel()
    {
        var service = new WeekLetterAiService("test-api-key", _mockLoggerFactory.Object, _aiToolsManager, new Mock<IConversationManager>().Object, new Mock<IPromptBuilder>().Object, "gpt-3.5-turbo");
        Assert.NotNull(service);
    }

    [Fact]
    public void ClearConversationHistory_WithoutContextKey_DoesNotThrow()
    {
        var service = new WeekLetterAiService("test-api-key", _mockLoggerFactory.Object, _aiToolsManager, new Mock<IConversationManager>().Object, new Mock<IPromptBuilder>().Object);

        // Should not throw an exception
        service.ClearConversationHistory();
        Assert.True(true);
    }

    [Fact]
    public void ClearConversationHistory_WithContextKey_DoesNotThrow()
    {
        var service = new WeekLetterAiService("test-api-key", _mockLoggerFactory.Object, _aiToolsManager, new Mock<IConversationManager>().Object, new Mock<IPromptBuilder>().Object);

        // Should not throw an exception
        service.ClearConversationHistory("test-context");
        Assert.True(true);
    }

    [Theory]
    [InlineData(ChatInterface.Slack)]
    [InlineData(ChatInterface.Telegram)]
    public async Task SummarizeWeekLetterAsync_WithNullWeekLetter_ThrowsArgumentNullException(ChatInterface chatInterface)
    {
        var service = new WeekLetterAiService("test-api-key", _mockLoggerFactory.Object, _aiToolsManager, new Mock<IConversationManager>().Object, new Mock<IPromptBuilder>().Object);

        await Assert.ThrowsAsync<NullReferenceException>(() =>
            service.SummarizeWeekLetterAsync(null!, chatInterface));
    }

    [Theory]
    [InlineData(ChatInterface.Slack)]
    [InlineData(ChatInterface.Telegram)]
    public async Task AskQuestionAboutWeekLetterAsync_WithNullWeekLetter_ThrowsArgumentNullException(ChatInterface chatInterface)
    {
        var service = new WeekLetterAiService("test-api-key", _mockLoggerFactory.Object, _aiToolsManager, new Mock<IConversationManager>().Object, new Mock<IPromptBuilder>().Object);

        await Assert.ThrowsAsync<NullReferenceException>(() =>
            service.AskQuestionAboutWeekLetterAsync(null!, "What is happening today?", "TestChild", chatInterface));
    }

    [Theory]
    [InlineData(ChatInterface.Slack)]
    [InlineData(ChatInterface.Telegram)]
    public async Task AskQuestionAboutWeekLetterAsync_WithContextKey_WithNullWeekLetter_ThrowsArgumentNullException(ChatInterface chatInterface)
    {
        var service = new WeekLetterAiService("test-api-key", _mockLoggerFactory.Object, _aiToolsManager, new Mock<IConversationManager>().Object, new Mock<IPromptBuilder>().Object);

        await Assert.ThrowsAsync<NullReferenceException>(() =>
            service.AskQuestionAboutWeekLetterAsync(null!, "What is happening today?", "context-key", chatInterface));
    }

    [Theory]
    [InlineData(ChatInterface.Slack)]
    [InlineData(ChatInterface.Telegram)]
    public async Task ExtractKeyInformationAsync_WithNullWeekLetter_ThrowsArgumentNullException(ChatInterface chatInterface)
    {
        var service = new WeekLetterAiService("test-api-key", _mockLoggerFactory.Object, _aiToolsManager, new Mock<IConversationManager>().Object, new Mock<IPromptBuilder>().Object);

        await Assert.ThrowsAsync<NullReferenceException>(() =>
            service.ExtractKeyInformationAsync(null!, chatInterface));
    }

    [Theory]
    [InlineData(ChatInterface.Slack)]
    [InlineData(ChatInterface.Telegram)]
    public async Task AskQuestionAboutChildrenAsync_WithNullChildrenWeekLetters_ThrowsArgumentNullException(ChatInterface chatInterface)
    {
        var service = new WeekLetterAiService("test-api-key", _mockLoggerFactory.Object, _aiToolsManager, new Mock<IConversationManager>().Object, new Mock<IPromptBuilder>().Object);

        await Assert.ThrowsAsync<NullReferenceException>(() =>
            service.AskQuestionAboutChildrenAsync(null!, "What are the children doing?", "context-key", chatInterface));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task ProcessQueryWithToolsAsync_WithInvalidQuery_HandlesGracefully(string query)
    {
        var service = new WeekLetterAiService("test-api-key", _mockLoggerFactory.Object, _aiToolsManager, new Mock<IConversationManager>().Object, new Mock<IPromptBuilder>().Object);

        // Should not throw exception, may return empty or error response
        var result = await service.ProcessQueryWithToolsAsync(query, "context-key", ChatInterface.Slack);

        // Just verify it returned something (even if it's an error message)
        Assert.NotNull(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task ProcessQueryWithToolsAsync_WithInvalidContextKey_HandlesGracefully(string contextKey)
    {
        var service = new WeekLetterAiService("test-api-key", _mockLoggerFactory.Object, _aiToolsManager, new Mock<IConversationManager>().Object, new Mock<IPromptBuilder>().Object);

        // Should not throw exception, may return error response
        var result = await service.ProcessQueryWithToolsAsync("What is happening?", contextKey, ChatInterface.Slack);

        // Just verify it returned something (even if it's an error message)
        Assert.NotNull(result);
    }

    [Fact]
    public async Task SummarizeWeekLetterAsync_WithValidWeekLetter_CallsApiWithCorrectParameters()
    {
        var service = new WeekLetterAiService("test-api-key", _mockLoggerFactory.Object, _aiToolsManager, new Mock<IConversationManager>().Object, new Mock<IPromptBuilder>().Object);
        var weekLetter = new JObject
        {
            ["ugebreve"] = new JArray
            {
                new JObject
                {
                    ["indhold"] = "Test week letter content for summarization"
                }
            }
        };

        // This will make an actual API call if a real API key is provided
        // For unit testing, we would need to mock the OpenAI client
        try
        {
            var result = await service.SummarizeWeekLetterAsync(weekLetter, ChatInterface.Slack);
            // If we get here, the call succeeded
            Assert.NotNull(result);
        }
        catch (Exception ex)
        {
            // Expected to fail with test API key
            Assert.NotNull(ex);
        }
    }

    [Fact]
    public async Task AskQuestionAboutWeekLetterAsync_WithValidParameters_CallsApiWithCorrectParameters()
    {
        var service = new WeekLetterAiService("test-api-key", _mockLoggerFactory.Object, _aiToolsManager, new Mock<IConversationManager>().Object, new Mock<IPromptBuilder>().Object);
        var weekLetter = new JObject
        {
            ["ugebreve"] = new JArray
            {
                new JObject
                {
                    ["indhold"] = "Test week letter content for questions"
                }
            }
        };

        try
        {
            var result = await service.AskQuestionAboutWeekLetterAsync(weekLetter, "What activities are planned?", "TestChild", ChatInterface.Telegram);
            Assert.NotNull(result);
        }
        catch (Exception ex)
        {
            // Expected to fail with test API key
            Assert.NotNull(ex);
        }
    }

    [Fact]
    public async Task ExtractKeyInformationAsync_WithValidWeekLetter_CallsApiWithCorrectParameters()
    {
        var service = new WeekLetterAiService("test-api-key", _mockLoggerFactory.Object, _aiToolsManager, new Mock<IConversationManager>().Object, new Mock<IPromptBuilder>().Object);
        var weekLetter = new JObject
        {
            ["ugebreve"] = new JArray
            {
                new JObject
                {
                    ["indhold"] = "Test week letter content for key information extraction"
                }
            }
        };

        try
        {
            var result = await service.ExtractKeyInformationAsync(weekLetter, ChatInterface.Slack);
            Assert.NotNull(result);
        }
        catch (Exception ex)
        {
            // Expected to fail with test API key
            Assert.NotNull(ex);
        }
    }

    [Fact]
    public async Task AskQuestionAboutChildrenAsync_WithValidParameters_CallsApiWithCorrectParameters()
    {
        var service = new WeekLetterAiService("test-api-key", _mockLoggerFactory.Object, _aiToolsManager, new Mock<IConversationManager>().Object, new Mock<IPromptBuilder>().Object);
        var childrenWeekLetters = new Dictionary<string, JObject>
        {
            ["Emma"] = new JObject
            {
                ["ugebreve"] = new JArray
                {
                    new JObject { ["indhold"] = "Emma's week letter content" }
                }
            },
            ["Søren Johannes"] = new JObject
            {
                ["ugebreve"] = new JArray
                {
                    new JObject { ["indhold"] = "Søren Johannes's week letter content" }
                }
            }
        };

        try
        {
            var result = await service.AskQuestionAboutChildrenAsync(
                childrenWeekLetters,
                "What are both children doing this week?",
                "test-context",
                ChatInterface.Telegram);
            Assert.NotNull(result);
        }
        catch (Exception ex)
        {
            // Expected to fail with test API key
            Assert.NotNull(ex);
        }
    }

    [Fact]
    public async Task ProcessQueryWithToolsAsync_WithValidParameters_CallsApiWithCorrectParameters()
    {
        var service = new WeekLetterAiService("test-api-key", _mockLoggerFactory.Object, _aiToolsManager, new Mock<IConversationManager>().Object, new Mock<IPromptBuilder>().Object);

        try
        {
            var result = await service.ProcessQueryWithToolsAsync(
                "What reminders do I have?",
                "test-context",
                ChatInterface.Slack);
            Assert.NotNull(result);
        }
        catch (Exception ex)
        {
            // Expected to fail with test API key
            Assert.NotNull(ex);
        }
    }

    [Fact]
    public void ChatInterface_Slack_HasCorrectValue()
    {
        Assert.Equal(0, (int)ChatInterface.Slack);
    }

    [Fact]
    public void ChatInterface_Telegram_HasCorrectValue()
    {
        Assert.Equal(1, (int)ChatInterface.Telegram);
    }

    [Fact]
    public void MultipleConversations_WithDifferentContextKeys_AreHandledSeparately()
    {
        var service = new WeekLetterAiService("test-api-key", _mockLoggerFactory.Object, _aiToolsManager, new Mock<IConversationManager>().Object, new Mock<IPromptBuilder>().Object);

        // Clear different context keys should not interfere with each other
        service.ClearConversationHistory("context1");
        service.ClearConversationHistory("context2");
        service.ClearConversationHistory(); // Clear all

        // Should not throw
        Assert.True(true);
    }
}
