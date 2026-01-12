using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using MinUddannelse.Client;
using MinUddannelse.GoogleCalendar;
using MinUddannelse.Security;
using MinUddannelse.AI.Services;
using MinUddannelse.Content.WeekLetters;
using MinUddannelse.Models;
using MinUddannelse.Repositories.DTOs;
using MinUddannelse;
using MinUddannelse.Configuration;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace MinUddannelse.Tests.AI.Services;

public class AgentServiceTests
{
    private readonly Mock<ILoggerFactory> _mockLoggerFactory;
    private readonly Mock<ILogger> _mockLogger;
    private readonly Mock<WeekLetterCache> _mockWeekLetterCache;
    private readonly Mock<IWeekLetterAiService> _mockOpenAiService;
    private readonly Mock<IMinUddannelseClient> _mockMinUddannelseClient;
    private readonly Config _config;

    public AgentServiceTests()
    {
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLogger = new Mock<ILogger>();
        _mockOpenAiService = new Mock<IWeekLetterAiService>();
        _mockMinUddannelseClient = new Mock<IMinUddannelseClient>();

        _config = new Config
        {
            MinUddannelse = new MinUddannelseConfig
            {
                Children = new List<Child> { new Child { FirstName = "Test", LastName = "Child" } }
            }
        };

        var mockCache = new Mock<IMemoryCache>();
        _mockWeekLetterCache = new Mock<WeekLetterCache>(mockCache.Object, _config, _mockLoggerFactory.Object);

        _mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(_mockLogger.Object);
    }

    [Fact]
    public void Constructor_WithValidParameters_InitializesCorrectly()
    {
        // Arrange & Act
        var service = new AgentService(_mockMinUddannelseClient.Object, _mockWeekLetterCache.Object,
            _config, _mockOpenAiService.Object, _mockLoggerFactory.Object);

        // Assert
        Assert.NotNull(service);
    }

    [Fact]
    public void Constructor_WithNullMinUddannelseClient_ThrowsArgumentNullException()
    {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new AgentService(null!, _mockWeekLetterCache.Object, _config, _mockOpenAiService.Object, _mockLoggerFactory.Object));
    }

    [Fact]
    public void Constructor_WithNullWeekLetterCache_ThrowsArgumentNullException()
    {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new AgentService(_mockMinUddannelseClient.Object, null!, _config, _mockOpenAiService.Object, _mockLoggerFactory.Object));
    }

    [Fact]
    public void Constructor_WithNullOpenAiService_ThrowsArgumentNullException()
    {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new AgentService(_mockMinUddannelseClient.Object, _mockWeekLetterCache.Object, _config, null!, _mockLoggerFactory.Object));
    }

    [Fact]
    public void Constructor_WithNullLoggerFactory_ThrowsArgumentNullException()
    {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new AgentService(_mockMinUddannelseClient.Object, _mockWeekLetterCache.Object, _config, _mockOpenAiService.Object, null!));
    }

    [Fact]
    public void AgentService_ImplementsIAgentServiceInterface()
    {
        // Arrange & Act & Assert
        Assert.True(typeof(IAgentService).IsAssignableFrom(typeof(AgentService)));
    }

    [Fact]
    public void AgentService_HasCorrectPublicMethods()
    {
        // Arrange
        var serviceType = typeof(AgentService);

        // Act & Assert
        Assert.NotNull(serviceType.GetMethod("GetWeekLetterAsync"));
        Assert.NotNull(serviceType.GetMethod("GetWeekScheduleAsync"));
        Assert.NotNull(serviceType.GetMethod("SummarizeWeekLetterAsync"));
        Assert.NotNull(serviceType.GetMethod("ExtractKeyInformationFromWeekLetterAsync"));
        Assert.NotNull(serviceType.GetMethod("GetChildByNameAsync"));
        Assert.NotNull(serviceType.GetMethod("GetAllChildrenAsync"));
        Assert.NotNull(serviceType.GetMethod("AskQuestionAboutChildrenAsync"));
        Assert.NotNull(serviceType.GetMethod("ProcessQueryWithToolsAsync"));
    }

    [Fact]
    public void AgentService_HasCorrectNamespace()
    {
        // Arrange
        var serviceType = typeof(AgentService);

        // Act & Assert
        Assert.Equal("MinUddannelse.AI.Services", serviceType.Namespace);
    }

    [Fact]
    public void AgentService_IsPublicClass()
    {
        // Arrange
        var serviceType = typeof(AgentService);

        // Act & Assert
        Assert.True(serviceType.IsPublic);
        Assert.False(serviceType.IsAbstract);
        Assert.False(serviceType.IsSealed);
    }

    [Fact]
    public void AgentService_ConstructorParametersHaveCorrectTypes()
    {
        // Arrange
        var serviceType = typeof(AgentService);
        var constructor = serviceType.GetConstructors()[0];

        // Act
        var parameters = constructor.GetParameters();

        // Assert
        Assert.Equal(5, parameters.Length);
        Assert.Equal(typeof(IMinUddannelseClient), parameters[0].ParameterType);
        Assert.Equal(typeof(WeekLetterCache), parameters[1].ParameterType);
        Assert.Equal(typeof(Config), parameters[2].ParameterType);
        Assert.Equal(typeof(IWeekLetterAiService), parameters[3].ParameterType);
        Assert.Equal(typeof(ILoggerFactory), parameters[4].ParameterType);
    }

    [Fact]
    public void AgentService_ConstructorParametersHaveCorrectNames()
    {
        // Arrange
        var serviceType = typeof(AgentService);
        var constructor = serviceType.GetConstructors()[0];

        // Act
        var parameters = constructor.GetParameters();

        // Assert
        Assert.Equal("minUddannelseClient", parameters[0].Name);
        Assert.Equal("dataService", parameters[1].Name);
        Assert.Equal("config", parameters[2].Name);
        Assert.Equal("openAiService", parameters[3].Name);
        Assert.Equal("loggerFactory", parameters[4].Name);
    }


    [Fact]
    public async Task GetWeekLetterAsync_WithCache_ReturnsCachedData()
    {
        // Arrange
        var cachedData = new JObject { ["cached"] = "data" };
        var child = new Child { FirstName = "Test", LastName = "Child" };
        _mockWeekLetterCache.Setup(x => x.GetWeekLetter(It.IsAny<Child>(), It.IsAny<int>(), It.IsAny<int>())).Returns(cachedData);

        var service = new AgentService(_mockMinUddannelseClient.Object, _mockWeekLetterCache.Object,
            _config, _mockOpenAiService.Object, _mockLoggerFactory.Object);

        // Act
        var result = await service.GetWeekLetterAsync(child, DateOnly.FromDateTime(DateTime.Today), useCache: true);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("data", result["cached"]?.ToString());
        _mockWeekLetterCache.Verify(x => x.GetWeekLetter(It.IsAny<Child>(), It.IsAny<int>(), It.IsAny<int>()), Times.Once());
        _mockMinUddannelseClient.Verify(x => x.GetWeekLetter(It.IsAny<Child>(), It.IsAny<DateOnly>(), It.IsAny<bool>()), Times.Never());
    }

    [Fact]
    public async Task GetWeekLetterAsync_WithoutCache_CallsMinUddannelseClient()
    {
        // Arrange - use valid week letter structure so it gets cached
        var freshData = new JObject
        {
            ["ugebreve"] = new JArray
            {
                new JObject
                {
                    ["indhold"] = "Valid week letter content for testing"
                }
            }
        };
        var child = new Child { FirstName = "Test", LastName = "Child" };
        _mockWeekLetterCache.Setup(x => x.GetWeekLetter(It.IsAny<Child>(), It.IsAny<int>(), It.IsAny<int>())).Returns((JObject?)null);
        _mockMinUddannelseClient.Setup(x => x.GetWeekLetter(It.IsAny<Child>(), It.IsAny<DateOnly>(), It.IsAny<bool>())).ReturnsAsync(freshData);

        var service = new AgentService(_mockMinUddannelseClient.Object, _mockWeekLetterCache.Object,
            _config, _mockOpenAiService.Object, _mockLoggerFactory.Object);

        // Act
        var result = await service.GetWeekLetterAsync(child, DateOnly.FromDateTime(DateTime.Today), useCache: true);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Test", result["child"]?.ToString());
        _mockMinUddannelseClient.Verify(x => x.GetWeekLetter(It.IsAny<Child>(), It.IsAny<DateOnly>(), It.IsAny<bool>()), Times.Once());
        _mockWeekLetterCache.Verify(x => x.CacheWeekLetter(It.IsAny<Child>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<JObject>()), Times.Once());
    }

    [Fact]
    public async Task GetWeekLetterAsync_CallsMinUddannelseClientDirectly()
    {
        // Arrange
        var child = new Child { FirstName = "Test", LastName = "Child" };
        _mockWeekLetterCache.Setup(x => x.GetWeekLetter(It.IsAny<Child>(), It.IsAny<int>(), It.IsAny<int>())).Returns((JObject?)null);
        _mockMinUddannelseClient.Setup(x => x.GetWeekLetter(It.IsAny<Child>(), It.IsAny<DateOnly>(), It.IsAny<bool>()))
            .ReturnsAsync(new JObject());

        var service = new AgentService(_mockMinUddannelseClient.Object, _mockWeekLetterCache.Object,
            _config, _mockOpenAiService.Object, _mockLoggerFactory.Object);

        // Act
        await service.GetWeekLetterAsync(child, DateOnly.FromDateTime(DateTime.Today), useCache: false);

        // Assert - Should directly call GetWeekLetter
        _mockMinUddannelseClient.Verify(x => x.GetWeekLetter(It.IsAny<Child>(), It.IsAny<DateOnly>(), It.IsAny<bool>()), Times.Once());
    }

    [Fact]
    public async Task GetWeekScheduleAsync_WithCache_ReturnsCachedData()
    {
        // Arrange
        var cachedSchedule = new JObject { ["schedule"] = "cached" };
        var child = new Child { FirstName = "Test", LastName = "Child" };
        _mockWeekLetterCache.Setup(x => x.GetWeekSchedule(It.IsAny<Child>(), It.IsAny<int>(), It.IsAny<int>())).Returns(cachedSchedule);

        var service = new AgentService(_mockMinUddannelseClient.Object, _mockWeekLetterCache.Object,
            _config, _mockOpenAiService.Object, _mockLoggerFactory.Object);

        // Act
        var result = await service.GetWeekScheduleAsync(child, DateOnly.FromDateTime(DateTime.Today), useCache: true);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("cached", result["schedule"]?.ToString());
        _mockWeekLetterCache.Verify(x => x.GetWeekSchedule(It.IsAny<Child>(), It.IsAny<int>(), It.IsAny<int>()), Times.Once());
        _mockMinUddannelseClient.Verify(x => x.GetWeekSchedule(It.IsAny<Child>(), It.IsAny<DateOnly>()), Times.Never());
    }

    [Fact]
    public async Task GetWeekScheduleAsync_WithoutCache_ReturnsNull()
    {
        // Arrange
        var child = new Child { FirstName = "Test", LastName = "Child" };
        _mockWeekLetterCache.Setup(x => x.GetWeekSchedule(It.IsAny<Child>(), It.IsAny<int>(), It.IsAny<int>())).Returns((JObject?)null);

        var service = new AgentService(_mockMinUddannelseClient.Object, _mockWeekLetterCache.Object,
            _config, _mockOpenAiService.Object, _mockLoggerFactory.Object);

        // Act
        var result = await service.GetWeekScheduleAsync(child, DateOnly.FromDateTime(DateTime.Today), useCache: true);

        // Assert
        Assert.Null(result);
        _mockMinUddannelseClient.Verify(x => x.GetWeekSchedule(It.IsAny<Child>(), It.IsAny<DateOnly>()), Times.Never());
        _mockWeekLetterCache.Verify(x => x.CacheWeekSchedule(It.IsAny<Child>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<JObject>()), Times.Never());
    }
}
