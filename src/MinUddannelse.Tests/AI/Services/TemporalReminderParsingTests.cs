using Microsoft.Extensions.Logging;
using MinUddannelse.AI.Services;
using MinUddannelse.Repositories;
using Moq;
using Newtonsoft.Json.Linq;
using System.Reflection;
using Xunit;

namespace MinUddannelse.Tests.AI.Services;

public class TemporalReminderParsingTests
{
    private readonly Mock<ILogger<WeekLetterReminderService>> _mockLogger;
    private readonly Mock<IWeekLetterRepository> _mockWeekLetterRepository;
    private readonly Mock<IReminderRepository> _mockReminderRepository;
    private readonly Mock<IOpenAiService> _mockOpenAiService;
    private readonly WeekLetterReminderService _service;

    public TemporalReminderParsingTests()
    {
        _mockLogger = new Mock<ILogger<WeekLetterReminderService>>();
        _mockWeekLetterRepository = new Mock<IWeekLetterRepository>();
        _mockReminderRepository = new Mock<IReminderRepository>();
        _mockOpenAiService = new Mock<IOpenAiService>();

        _service = new WeekLetterReminderService(
            _mockOpenAiService.Object,
            _mockLogger.Object,
            _mockWeekLetterRepository.Object,
            _mockReminderRepository.Object,
            "gpt-4o-mini",
            TimeOnly.Parse("06:45"));
    }

    [Fact]
    public void ParseExtractedEvents_WithNewTemporalFormat_ParsesCorrectly()
    {
        // Arrange
        var jsonResponse = @"{
            ""this_week"": [
                {
                    ""day"": ""onsdag"",
                    ""title"": ""Haver til maver"",
                    ""description"": ""Afslutning af vores forløb i Haver til Maver - eleverne skal have godt tøj på"",
                    ""date"": ""2025-10-22"",
                    ""type"": ""event"",
                    ""confidence"": 0.95
                }
            ],
            ""future"": [
                {
                    ""title"": ""Overnatning"",
                    ""description"": ""Overnatning i Ganløsehytten den 5. og 6. november"",
                    ""date"": ""2025-11-05"",
                    ""type"": ""event"",
                    ""confidence"": 0.955
                }
            ]
        }";

        // Act
        var result = CallParseExtractedEvents(jsonResponse);

        // Assert
        Assert.Equal(2, result.Count);

        // Current week event
        var currentWeekEvent = result.FirstOrDefault(e => e.IsCurrentWeek);
        Assert.NotNull(currentWeekEvent);
        Assert.Equal("Haver til maver", currentWeekEvent.Title);
        Assert.Equal("onsdag", currentWeekEvent.DayOfWeek);
        Assert.Equal(new DateTime(2025, 10, 22), currentWeekEvent.EventDate);
        Assert.True(currentWeekEvent.IsCurrentWeek);

        // Future event
        var futureEvent = result.FirstOrDefault(e => !e.IsCurrentWeek);
        Assert.NotNull(futureEvent);
        Assert.Equal("Overnatning", futureEvent.Title);
        Assert.Null(futureEvent.DayOfWeek);
        Assert.Equal(new DateTime(2025, 11, 5), futureEvent.EventDate);
        Assert.False(futureEvent.IsCurrentWeek);
    }

    [Fact]
    public void ParseExtractedEvents_WithEmptyArrays_ReturnsEmptyList()
    {
        // Arrange
        var jsonResponse = @"{
            ""this_week"": [],
            ""future"": []
        }";

        // Act
        var result = CallParseExtractedEvents(jsonResponse);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void ParseExtractedEvents_WithMissingArrays_HandlesGracefully()
    {
        // Arrange
        var jsonResponse = @"{}";

        // Act
        var result = CallParseExtractedEvents(jsonResponse);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void ParseExtractedEvents_WithLowConfidenceEvents_FiltersOut()
    {
        // Arrange
        var jsonResponse = @"{
            ""this_week"": [
                {
                    ""day"": ""torsdag"",
                    ""title"": ""Low confidence event"",
                    ""description"": ""This should be filtered out"",
                    ""date"": ""2025-10-23"",
                    ""type"": ""event"",
                    ""confidence"": 0.5
                }
            ],
            ""future"": [
                {
                    ""title"": ""High confidence event"",
                    ""description"": ""This should be included"",
                    ""date"": ""2025-11-15"",
                    ""type"": ""event"",
                    ""confidence"": 0.955
                }
            ]
        }";

        // Act
        var result = CallParseExtractedEvents(jsonResponse);

        // Assert
        Assert.Single(result);
        Assert.Equal("High confidence event", result.First().Title);
        Assert.False(result.First().IsCurrentWeek);
    }

    [Fact]
    public void ParseExtractedEvents_WithMultipleEvents_ParsesAllCorrectly()
    {
        // Arrange
        var jsonResponse = @"{
            ""this_week"": [
                {
                    ""day"": ""mandag"",
                    ""title"": ""Event 1"",
                    ""description"": ""Monday event"",
                    ""date"": ""2025-10-20"",
                    ""type"": ""event"",
                    ""confidence"": 0.95
                },
                {
                    ""day"": ""fredag"",
                    ""title"": ""Event 2"",
                    ""description"": ""Friday event"",
                    ""date"": ""2025-10-24"",
                    ""type"": ""deadline"",
                    ""confidence"": 0.955
                }
            ],
            ""future"": [
                {
                    ""title"": ""Future Event 1"",
                    ""description"": ""November event"",
                    ""date"": ""2025-11-10"",
                    ""type"": ""event"",
                    ""confidence"": 0.95
                },
                {
                    ""title"": ""Future Event 2"",
                    ""description"": ""December event"",
                    ""date"": ""2025-12-05"",
                    ""type"": ""permission_form"",
                    ""confidence"": 0.955
                }
            ]
        }";

        // Act
        var result = CallParseExtractedEvents(jsonResponse);

        // Assert
        Assert.Equal(4, result.Count);
        Assert.Equal(2, result.Count(e => e.IsCurrentWeek));
        Assert.Equal(2, result.Count(e => !e.IsCurrentWeek));

        // Verify current week events have day names
        var currentWeekEvents = result.Where(e => e.IsCurrentWeek).ToList();
        Assert.All(currentWeekEvents, e => Assert.NotNull(e.DayOfWeek));
        Assert.Contains(currentWeekEvents, e => e.DayOfWeek == "mandag");
        Assert.Contains(currentWeekEvents, e => e.DayOfWeek == "fredag");

        // Verify future events don't have day names
        var futureEvents = result.Where(e => !e.IsCurrentWeek).ToList();
        Assert.All(futureEvents, e => Assert.Null(e.DayOfWeek));
    }

    [Theory]
    [InlineData("event")]
    [InlineData("deadline")]
    [InlineData("permission_form")]
    [InlineData("supply_needed")]
    public void ParseExtractedEvents_WithDifferentEventTypes_PreservesType(string eventType)
    {
        // Arrange
        var jsonResponse = $@"{{
            ""this_week"": [],
            ""future"": [
                {{
                    ""title"": ""Test Event"",
                    ""description"": ""Test description"",
                    ""date"": ""2025-11-10"",
                    ""type"": ""{eventType}"",
                    ""confidence"": 0.95
                }}
            ]
        }}";

        // Act
        var result = CallParseExtractedEvents(jsonResponse);

        // Assert
        Assert.Single(result);
        Assert.Equal(eventType, result.First().EventType);
    }

    [Fact]
    public void ParseExtractedEvents_WithHtmlEncodedContent_DecodesCorrectly()
    {
        // Arrange
        var jsonResponse = @"{
            ""this_week"": [
                {
                    ""day"": ""onsdag"",
                    ""title"": ""Test &amp; Event"",
                    ""description"": ""Description with &quot;quotes&quot; and &lt;tags&gt;"",
                    ""date"": ""2025-10-22"",
                    ""type"": ""event"",
                    ""confidence"": 0.95
                }
            ],
            ""future"": []
        }";

        // Act
        var result = CallParseExtractedEvents(jsonResponse);

        // Assert
        Assert.Single(result);
        Assert.Equal("Test & Event", result.First().Title);
        Assert.Equal("Description with \"quotes\" and <tags>", result.First().Description);
    }

    [Fact]
    public void ParseExtractedEvents_WithInvalidJson_ReturnsEmptyList()
    {
        // Arrange
        var invalidJson = "{ invalid json }";

        // Act
        var result = CallParseExtractedEvents(invalidJson);

        // Assert
        Assert.Empty(result);
    }

    /// <summary>
    /// Helper method to call the private ParseExtractedEvents method via reflection
    /// </summary>
    private List<ExtractedEvent> CallParseExtractedEvents(string jsonResponse)
    {
        var method = typeof(WeekLetterReminderService).GetMethod("ParseExtractedEvents",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        var result = method.Invoke(_service, new object[] { jsonResponse });
        return (List<ExtractedEvent>)result!;
    }
}
