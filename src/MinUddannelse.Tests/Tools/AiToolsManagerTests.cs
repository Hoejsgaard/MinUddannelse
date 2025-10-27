using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json.Linq;
using MinUddannelse.AI.Services;
using MinUddannelse.Repositories;
using MinUddannelse.Configuration;
using MinUddannelse.Content.WeekLetters;
using MinUddannelse.Models;
using MinUddannelse.Repositories.DTOs;
using MinUddannelse.Security;
using MinUddannelse;

namespace MinUddannelse.Tests.Tools;

public class AiToolsManagerTests
{
    private readonly Mock<IReminderRepository> _mockReminderRepository;
    private readonly Mock<IScheduledTaskRepository> _mockScheduledTaskRepository;
    private readonly Mock<WeekLetterCache> _mockDataManager;
    private readonly ILoggerFactory _loggerFactory;
    private readonly AiToolsManager _aiToolsManager;
    private readonly List<Child> _testChildren;
    private readonly Config _config;

    public AiToolsManagerTests()
    {
        _mockReminderRepository = new Mock<IReminderRepository>();
        _mockScheduledTaskRepository = new Mock<IScheduledTaskRepository>();
        _loggerFactory = new LoggerFactory();

        _testChildren = new List<Child>
        {
            new Child { FirstName = "Alice", LastName = "Johnson" },
            new Child { FirstName = "Bob", LastName = "Smith" }
        };

        _config = new Config
        {
            MinUddannelse = new MinUddannelseConfig
            {
                Children = _testChildren
            }
        };

        var mockCache = new Mock<IMemoryCache>();
        _mockDataManager = new Mock<WeekLetterCache>(mockCache.Object, _config, _loggerFactory);

        _aiToolsManager = new AiToolsManager(_mockReminderRepository.Object, _mockScheduledTaskRepository.Object, _mockDataManager.Object, _config, _loggerFactory);
    }

    [Fact]
    public async Task CreateReminderAsync_WithValidDateTime_CreatesReminder()
    {
        // Arrange
        var description = "Pick up Alice from school";
        var dateTime = DateTime.Now.AddDays(1).ToString("yyyy-MM-dd HH:mm");
        var childName = "Alice";

        _mockReminderRepository.Setup(s => s.AddReminderAsync(
            It.IsAny<string>(),
            It.IsAny<DateOnly>(),
            It.IsAny<TimeOnly>(),
            It.IsAny<string>()))
            .ReturnsAsync(123);

        // Act
        var result = await _aiToolsManager.CreateReminderAsync(description, dateTime, childName);

        // Assert
        Assert.Contains("✅", result);
        Assert.Contains("Pick up Alice from school", result);
        _mockReminderRepository.Verify(s => s.AddReminderAsync(
            description,
            It.IsAny<DateOnly>(),
            It.IsAny<TimeOnly>(),
            childName), Times.Once());
    }

    [Fact]
    public async Task CreateReminderAsync_WithInvalidDateTime_ReturnsError()
    {
        // Arrange
        var description = "Invalid reminder";
        var invalidDateTime = "not-a-date";
        var childName = "Alice";

        // Act
        var result = await _aiToolsManager.CreateReminderAsync(description, invalidDateTime, childName);

        // Assert
        Assert.Contains("Error: Invalid date format", result);
        _mockReminderRepository.Verify(s => s.AddReminderAsync(
            It.IsAny<string>(),
            It.IsAny<DateOnly>(),
            It.IsAny<TimeOnly>(),
            It.IsAny<string>()), Times.Never());
    }

    [Fact]
    public async Task ListRemindersAsync_WithReminders_ReturnsFormattedList()
    {
        // Arrange
        var testReminders = new List<Reminder>
        {
            new Reminder
            {
                Id = 1,
                Text = "Reminder 1",
                RemindDate = DateOnly.FromDateTime(DateTime.Today.AddDays(1)),
                RemindTime = new TimeOnly(9, 0),
                ChildName = "Alice",
                IsSent = false
            },
            new Reminder
            {
                Id = 2,
                Text = "Reminder 2",
                RemindDate = DateOnly.FromDateTime(DateTime.Today.AddDays(2)),
                RemindTime = new TimeOnly(14, 30),
                ChildName = "Bob",
                IsSent = true
            }
        };

        _mockReminderRepository.Setup(s => s.GetAllRemindersAsync())
            .ReturnsAsync(testReminders);

        // Act
        var result = await _aiToolsManager.ListRemindersAsync();

        // Assert
        Assert.Contains("Active reminders:", result);
        Assert.Contains("Reminder 1", result);
        Assert.Contains("Reminder 2", result);
        Assert.Contains("(Alice)", result);
        Assert.Contains("(Bob)", result);
    }

    [Fact]
    public async Task ListRemindersAsync_WithNoReminders_ReturnsEmptyMessage()
    {
        // Arrange
        _mockReminderRepository.Setup(s => s.GetAllRemindersAsync())
            .ReturnsAsync(new List<Reminder>());

        // Act
        var result = await _aiToolsManager.ListRemindersAsync();

        // Assert
        Assert.Contains("No active reminders found", result);
    }

    [Fact]
    public async Task DeleteReminderAsync_WithValidNumber_DeletesReminder()
    {
        // Arrange
        var reminderNumber = 1; // 1-based index, not ID
        var testReminders = new List<Reminder>
        {
            new Reminder { Id = 123, Text = "Test reminder to delete", RemindDate = DateOnly.FromDateTime(DateTime.Today), RemindTime = new TimeOnly(10, 0) }
        };

        _mockReminderRepository.Setup(s => s.GetAllRemindersAsync())
            .ReturnsAsync(testReminders);

        // Act
        var result = await _aiToolsManager.DeleteReminderAsync(reminderNumber);

        // Assert
        Assert.Contains("✅ Deleted reminder", result);
        _mockReminderRepository.Verify(s => s.DeleteReminderAsync(123), Times.Once()); // Verify actual ID is used
    }

    [Fact]
    public async Task DeleteReminderAsync_WithInvalidReminderNumber_ReturnsError()
    {
        // Arrange
        var invalidReminderNumber = 999; // 1-based index that doesn't exist in list
        _mockReminderRepository.Setup(s => s.GetAllRemindersAsync())
            .ReturnsAsync(new List<Reminder>());

        // Act
        var result = await _aiToolsManager.DeleteReminderAsync(invalidReminderNumber);

        // Assert
        Assert.Contains("❌ Invalid reminder number", result);
        _mockReminderRepository.Verify(s => s.DeleteReminderAsync(It.IsAny<int>()), Times.Never());
    }

    [Fact]
    public void GetWeekLetters_WithChildName_ReturnsFilteredResults()
    {
        // Arrange
        var weekLetter = new JObject
        {
            ["ugebreve"] = new JArray
            {
                new JObject
                {
                    ["klasseNavn"] = "3A",
                    ["uge"] = "42",
                    ["indhold"] = "Test week letter content for Alice"
                }
            }
        };

        _mockDataManager.Setup(d => d.GetWeekLetter(It.Is<Child>(c => c.FirstName == "Alice"), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(weekLetter);
        _mockDataManager.Setup(d => d.GetWeekLetter(It.Is<Child>(c => c.FirstName == "Bob"), It.IsAny<int>(), It.IsAny<int>()))
            .Returns((JObject?)null);

        // Act
        var result = _aiToolsManager.GetWeekLetters("Alice");

        // Assert
        Assert.Contains("Alice Johnson", result);
        Assert.Contains("Week Letter", result);
        Assert.DoesNotContain("Bob Smith", result);
    }

    [Fact]
    public void GetWeekLetters_WithNonExistentChild_ReturnsError()
    {
        // Act
        var result = _aiToolsManager.GetWeekLetters("NonExistentChild");

        // Assert
        Assert.Contains("❌", result);
        Assert.Contains("No children found matching", result);
    }

    [Fact]
    public async Task CreateReminderAsync_WithMissingChildName_CreatesGeneralReminder()
    {
        // Arrange
        var description = "General reminder";
        var dateTime = DateTime.Now.AddDays(1).ToString("yyyy-MM-dd HH:mm");

        _mockReminderRepository.Setup(s => s.AddReminderAsync(
            It.IsAny<string>(),
            It.IsAny<DateOnly>(),
            It.IsAny<TimeOnly>(),
            It.IsAny<string>()))
            .ReturnsAsync(456);

        // Act
        var result = await _aiToolsManager.CreateReminderAsync(description, dateTime);

        // Assert
        Assert.Contains("✅", result);
        Assert.Contains("General reminder", result);
        _mockReminderRepository.Verify(s => s.AddReminderAsync(
            description,
            It.IsAny<DateOnly>(),
            It.IsAny<TimeOnly>(),
            null), Times.Once());
    }

    // Phase 1: High-Impact, Low-Effort Tests (+15% coverage)

    [Fact]
    public void GetCurrentDateTime_ReturnsCurrentTime()
    {
        // Act
        var result = _aiToolsManager.GetCurrentDateTime();

        // Assert
        Assert.NotNull(result);
        Assert.Contains(DateTime.Now.ToString("yyyy-MM-dd"), result);
        Assert.Contains("Today is", result);
    }

    [Fact]
    public void GetHelp_ReturnsHelpText()
    {
        // Act
        var result = _aiToolsManager.GetHelp();

        // Assert
        Assert.NotNull(result);
        Assert.Contains("Available Commands", result);
        Assert.Contains("Reminder", result);
    }

    [Fact]
    public void ExtractSummaryFromWeekLetter_WithSummaryField_ReturnsSummary()
    {
        // Arrange
        var weekLetter = JObject.Parse(@"{
			""summary"": ""This is a summary of the week"",
			""content"": ""This is the full content""
		}");

        // Use reflection to call the private method
        var method = typeof(AiToolsManager).GetMethod("ExtractSummaryFromWeekLetter",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        var result = (string)method!.Invoke(_aiToolsManager, new object[] { weekLetter })!;

        // Assert
        Assert.Equal("This is a summary of the week", result);
    }

    [Fact]
    public void ExtractSummaryFromWeekLetter_WithNullSummary_ReturnsEmptyString()
    {
        // Arrange
        var weekLetter = JObject.Parse(@"{
			""summary"": null,
			""content"": ""This is the content""
		}");

        // Use reflection to call the private method
        var method = typeof(AiToolsManager).GetMethod("ExtractSummaryFromWeekLetter",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        var result = (string)method!.Invoke(_aiToolsManager, new object[] { weekLetter })!;

        // Assert - Returns empty string when summary is null (due to JToken behavior)
        Assert.Equal("", result);
    }

    [Fact]
    public void ExtractSummaryFromWeekLetter_WithoutSummaryField_FallsBackToContent()
    {
        // Arrange
        var weekLetter = JObject.Parse(@"{
			""content"": ""This is the content""
		}");

        // Use reflection to call the private method
        var method = typeof(AiToolsManager).GetMethod("ExtractSummaryFromWeekLetter",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        var result = (string)method!.Invoke(_aiToolsManager, new object[] { weekLetter })!;

        // Assert - Should fall back to content extraction when summary field doesn't exist
        Assert.Equal("This is the content", result);
    }

    [Fact]
    public void ExtractSummaryFromWeekLetter_WithLongContent_TruncatesContent()
    {
        // Arrange
        var longContent = new string('A', 400); // 400 characters
        var weekLetter = JObject.Parse($@"{{
			""content"": ""{longContent}""
		}}");

        // Use reflection to call the private method
        var method = typeof(AiToolsManager).GetMethod("ExtractSummaryFromWeekLetter",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        var result = (string)method!.Invoke(_aiToolsManager, new object[] { weekLetter })!;

        // Assert
        Assert.Equal(303, result.Length); // 300 chars + "..."
        Assert.EndsWith("...", result);
        Assert.StartsWith(new string('A', 300), result);
    }

    [Fact]
    public void ExtractContentFromWeekLetter_WithContentField_ReturnsContent()
    {
        // Arrange
        var weekLetter = JObject.Parse(@"{
			""content"": ""This is the main content"",
			""text"": ""This is alternative text""
		}");

        // Use reflection to call the private method
        var method = typeof(AiToolsManager).GetMethod("ExtractContentFromWeekLetter",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        var result = (string)method!.Invoke(_aiToolsManager, new object[] { weekLetter })!;

        // Assert
        Assert.Equal("This is the main content", result);
    }

    [Fact]
    public void ExtractContentFromWeekLetter_WithNullContent_ReturnsEmptyString()
    {
        // Arrange
        var weekLetter = JObject.Parse(@"{
			""content"": null,
			""text"": ""This is the text field""
		}");

        // Use reflection to call the private method
        var method = typeof(AiToolsManager).GetMethod("ExtractContentFromWeekLetter",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        var result = (string)method!.Invoke(_aiToolsManager, new object[] { weekLetter })!;

        // Assert - Returns empty string when content is null
        Assert.Equal("", result);
    }

    [Fact]
    public void ExtractContentFromWeekLetter_WithTextField_ReturnsText()
    {
        // Arrange
        var weekLetter = JObject.Parse(@"{
			""text"": ""This is the text content""
		}");

        // Use reflection to call the private method
        var method = typeof(AiToolsManager).GetMethod("ExtractContentFromWeekLetter",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        var result = (string)method!.Invoke(_aiToolsManager, new object[] { weekLetter })!;

        // Assert
        Assert.Equal("This is the text content", result);
    }

    [Fact]
    public void ExtractContentFromWeekLetter_WithNullText_ReturnsEmptyString()
    {
        // Arrange
        var weekLetter = JObject.Parse(@"{
			""text"": null,
			""other"": ""some other data""
		}");

        // Use reflection to call the private method
        var method = typeof(AiToolsManager).GetMethod("ExtractContentFromWeekLetter",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        var result = (string)method!.Invoke(_aiToolsManager, new object[] { weekLetter })!;

        // Assert - Returns empty string when text is null
        Assert.Equal("", result);
    }

    [Fact]
    public void ExtractContentFromWeekLetter_WithNoContentOrText_ReturnsJsonString()
    {
        // Arrange
        var weekLetter = JObject.Parse(@"{
			""title"": ""Week 35"",
			""class"": ""3A""
		}");

        // Use reflection to call the private method
        var method = typeof(AiToolsManager).GetMethod("ExtractContentFromWeekLetter",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        var result = (string)method!.Invoke(_aiToolsManager, new object[] { weekLetter })!;

        // Assert - Should return JSON string representation
        Assert.NotNull(result);
        Assert.Contains("Week 35", result);
        Assert.Contains("3A", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    [InlineData("2024-13-45")] // Invalid date
    public async Task CreateReminderAsync_WithInvalidDateTime_ReturnsErrorMessage(string invalidDateTime)
    {
        // Act
        var result = await _aiToolsManager.CreateReminderAsync("Test reminder", invalidDateTime);

        // Assert
        Assert.Contains("Error: Invalid date format", result);
        Assert.Contains("yyyy-MM-dd HH:mm", result);
    }

    [Fact]
    public async Task DeleteReminderAsync_WithInvalidId_ReturnsErrorMessage()
    {
        // Arrange
        _mockReminderRepository.Setup(s => s.DeleteReminderAsync(999))
            .Returns(Task.CompletedTask);

        // Set up GetAllRemindersAsync to return empty list (simulating reminder not found)
        _mockReminderRepository.Setup(s => s.GetAllRemindersAsync())
            .ReturnsAsync(new List<Reminder>());

        // Act
        var result = await _aiToolsManager.DeleteReminderAsync(999);

        // Assert
        Assert.Contains("❌ Invalid reminder number", result);
        Assert.Contains("between 1 and 0", result);
    }

    [Fact]
    public async Task ListRemindersAsync_WithDatabaseError_ReturnsErrorMessage()
    {
        // Arrange
        _mockReminderRepository.Setup(s => s.GetAllRemindersAsync())
            .ThrowsAsync(new Exception("Database connection failed"));

        // Act
        var result = await _aiToolsManager.ListRemindersAsync();

        // Assert
        Assert.Contains("❌ Failed to retrieve reminders", result);
        Assert.Contains("Please try again", result);
    }

    [Fact]
    public async Task CreateReminderAsync_WithDatabaseError_ReturnsErrorMessage()
    {
        // Arrange
        var validDateTime = DateTime.Now.AddDays(1).ToString("yyyy-MM-dd HH:mm");
        _mockReminderRepository.Setup(s => s.AddReminderAsync(
            It.IsAny<string>(),
            It.IsAny<DateOnly>(),
            It.IsAny<TimeOnly>(),
            It.IsAny<string>()))
            .ThrowsAsync(new Exception("Database connection failed"));

        // Act
        var result = await _aiToolsManager.CreateReminderAsync("Test reminder", validDateTime);

        // Assert
        Assert.Contains("❌ Failed to create reminder", result);
        Assert.Contains("Please try again", result);
    }

    [Fact]
    public async Task DeleteReminderAsync_WithDatabaseError_ReturnsErrorMessage()
    {
        // Arrange
        _mockReminderRepository.Setup(s => s.DeleteReminderAsync(1))
            .ThrowsAsync(new Exception("Database connection failed"));

        // Act
        var result = await _aiToolsManager.DeleteReminderAsync(1);

        // Assert
        Assert.Contains("❌ Failed to delete reminder", result);
        Assert.Contains("Please try again", result);
    }

    [Fact]
    public async Task CreateReminderAsync_WithSupabaseException_ReturnsError()
    {
        // Arrange
        var description = "Test reminder";
        var dateTime = DateTime.Now.AddDays(1).ToString("yyyy-MM-dd HH:mm");
        var childName = "Alice";

        _mockReminderRepository.Setup(s => s.AddReminderAsync(
            It.IsAny<string>(),
            It.IsAny<DateOnly>(),
            It.IsAny<TimeOnly>(),
            It.IsAny<string>()))
            .ThrowsAsync(new Exception("Database connection failed"));

        // Act
        var result = await _aiToolsManager.CreateReminderAsync(description, dateTime, childName);

        // Assert
        Assert.Contains("❌ Failed to create reminder", result);
    }

    [Fact]
    public async Task ListRemindersAsync_WithSupabaseException_ReturnsError()
    {
        // Arrange
        _mockReminderRepository.Setup(s => s.GetAllRemindersAsync())
            .ThrowsAsync(new Exception("Database error"));

        // Act
        var result = await _aiToolsManager.ListRemindersAsync();

        // Assert
        Assert.Contains("❌ Failed to retrieve reminders", result);
    }

    [Fact]
    public async Task DeleteReminderAsync_WithSupabaseException_ReturnsError()
    {
        // Arrange
        var reminderNumber = 1;
        var testReminders = new List<Reminder>
        {
            new Reminder { Id = 123, Text = "Test reminder", RemindDate = DateOnly.FromDateTime(DateTime.Today), RemindTime = new TimeOnly(10, 0) }
        };

        _mockReminderRepository.Setup(s => s.GetAllRemindersAsync())
            .ReturnsAsync(testReminders);
        _mockReminderRepository.Setup(s => s.DeleteReminderAsync(123))
            .ThrowsAsync(new Exception("Delete operation failed"));

        // Act
        var result = await _aiToolsManager.DeleteReminderAsync(reminderNumber);

        // Assert
        Assert.Contains("❌ Failed to delete reminder", result);
    }

    [Fact]
    public void GetWeekLetters_WithWeekLetterCacheException_ReturnsError()
    {
        // Arrange
        _mockDataManager.Setup(d => d.GetWeekLetter(It.IsAny<Child>(), It.IsAny<int>(), It.IsAny<int>()))
            .Throws(new Exception("Data service error"));

        // Act
        var result = _aiToolsManager.GetWeekLetters("Alice");

        // Assert
        Assert.Contains("❌ Failed to retrieve week letters", result);
    }

    [Fact]
    public async Task ListRemindersAsync_WithChildNameFilter_ReturnsFiltered()
    {
        // Arrange
        var testReminders = new List<Reminder>
        {
            new Reminder
            {
                Id = 1,
                Text = "Alice reminder",
                RemindDate = DateOnly.FromDateTime(DateTime.Today.AddDays(1)),
                RemindTime = new TimeOnly(9, 0),
                ChildName = "Alice",
                IsSent = false
            },
            new Reminder
            {
                Id = 2,
                Text = "Bob reminder",
                RemindDate = DateOnly.FromDateTime(DateTime.Today.AddDays(2)),
                RemindTime = new TimeOnly(14, 30),
                ChildName = "Bob",
                IsSent = false
            }
        };

        _mockReminderRepository.Setup(s => s.GetAllRemindersAsync())
            .ReturnsAsync(testReminders);

        // Act
        var result = await _aiToolsManager.ListRemindersAsync("Alice");

        // Assert
        Assert.Contains("Alice reminder", result);
        Assert.DoesNotContain("Bob reminder", result);
        Assert.Contains("(Alice)", result);
        Assert.DoesNotContain("(Bob)", result);
    }

    // Phase 2: GetChildActivities Comprehensive Testing (+18% coverage)

    [Fact]
    public void GetChildActivities_WithValidChildAndDate_ReturnsActivities()
    {
        // Arrange
        var childName = "Alice";
        var dateString = "2024-01-15"; // Monday
        var weekLetter = CreateTestWeekLetterWithActivities();

        _mockDataManager.Setup(d => d.GetWeekLetter(It.Is<Child>(c => c.FirstName == "Alice"), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(weekLetter);

        // Act
        var result = _aiToolsManager.GetChildActivities(childName, dateString);

        // Assert
        Assert.Contains("Alice Johnson", result);
        Assert.Contains("Monday", result);
        Assert.Contains("Math lesson", result);
        Assert.Contains("Science project", result);
    }

    [Fact]
    public void GetChildActivities_WithValidChildAndNullDate_UsesToday()
    {
        // Arrange
        var childName = "Alice";
        var weekLetter = CreateTestWeekLetterWithActivities();

        _mockDataManager.Setup(d => d.GetWeekLetter(It.Is<Child>(c => c.FirstName == "Alice"), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(weekLetter);

        // Act
        var result = _aiToolsManager.GetChildActivities(childName, null);

        // Assert
        Assert.Contains("Alice Johnson", result);
        Assert.Contains(DateTime.Today.DayOfWeek.ToString(), result);
    }

    [Fact]
    public void GetChildActivities_WithInvalidDate_ReturnsError()
    {
        // Arrange
        var childName = "Alice";
        var invalidDate = "not-a-date";

        // Act
        var result = _aiToolsManager.GetChildActivities(childName, invalidDate);

        // Assert
        Assert.Contains("❌ Invalid date format", result);
    }

    [Fact]
    public void GetChildActivities_WithNonExistentChild_ReturnsError()
    {
        // Arrange
        var nonExistentChild = "NonExistentChild";
        var dateString = "2024-01-15";

        // Act
        var result = _aiToolsManager.GetChildActivities(nonExistentChild, dateString);

        // Assert
        Assert.Contains("❌ Child 'NonExistentChild' not found", result);
    }

    [Fact]
    public void GetChildActivities_WithNoWeekLetter_ReturnsMessage()
    {
        // Arrange
        var childName = "Alice";
        var dateString = "2024-01-15";

        _mockDataManager.Setup(d => d.GetWeekLetter(It.Is<Child>(c => c.FirstName == "Alice"), It.IsAny<int>(), It.IsAny<int>()))
            .Returns((JObject?)null);

        // Act
        var result = _aiToolsManager.GetChildActivities(childName, dateString);

        // Assert
        Assert.Contains("No week letter available", result);
        Assert.Contains("Alice", result);
    }

    [Fact]
    public void GetChildActivities_WithMatchingActivities_ReturnsFormatted()
    {
        // Arrange
        var childName = "Alice";
        var dateString = "2024-01-15"; // Monday
        var weekLetter = CreateTestWeekLetterWithActivities();

        _mockDataManager.Setup(d => d.GetWeekLetter(It.Is<Child>(c => c.FirstName == "Alice"), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(weekLetter);

        // Act
        var result = _aiToolsManager.GetChildActivities(childName, dateString);

        // Assert
        Assert.Contains("Alice Johnson", result);
        Assert.Contains("Math lesson", result);
        Assert.Contains("Science project", result);
        Assert.Contains("Monday", result);
    }

    [Fact]
    public void GetChildActivities_WithNoMatchingActivities_ReturnsMessage()
    {
        // Arrange
        var childName = "Alice";
        var dateString = "2024-01-20"; // Saturday - likely no school activities
        var weekLetter = CreateTestWeekLetterWithActivities();

        _mockDataManager.Setup(d => d.GetWeekLetter(It.Is<Child>(c => c.FirstName == "Alice"), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(weekLetter);

        // Act
        var result = _aiToolsManager.GetChildActivities(childName, dateString);

        // Assert
        Assert.Contains("No specific activities found", result);
        Assert.Contains("Alice", result);
        Assert.Contains("Saturday", result);
    }

    [Fact]
    public void GetChildActivities_WithException_ReturnsError()
    {
        // Arrange
        var childName = "Alice";
        var dateString = "2024-01-15";

        _mockDataManager.Setup(d => d.GetWeekLetter(It.IsAny<Child>(), It.IsAny<int>(), It.IsAny<int>()))
            .Throws(new Exception("Data access error"));

        // Act
        var result = _aiToolsManager.GetChildActivities(childName, dateString);

        // Assert
        Assert.Contains("❌ Failed to retrieve activities", result);
    }

    // Phase 3: Edge Cases and Complex Scenarios

    [Fact]
    public void GetWeekLetters_WithAllChildren_ReturnsAll()
    {
        // Arrange
        var aliceWeekLetter = new JObject
        {
            ["ugebreve"] = new JArray
            {
                new JObject
                {
                    ["klasseNavn"] = "3A",
                    ["uge"] = "42",
                    ["indhold"] = "Alice's week letter content"
                }
            }
        };

        var bobWeekLetter = new JObject
        {
            ["ugebreve"] = new JArray
            {
                new JObject
                {
                    ["klasseNavn"] = "4B",
                    ["uge"] = "42",
                    ["indhold"] = "Bob's week letter content"
                }
            }
        };

        _mockDataManager.Setup(d => d.GetWeekLetter(It.Is<Child>(c => c.FirstName == "Alice"), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(aliceWeekLetter);
        _mockDataManager.Setup(d => d.GetWeekLetter(It.Is<Child>(c => c.FirstName == "Bob"), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(bobWeekLetter);

        // Act - null childName should return all children
        var result = _aiToolsManager.GetWeekLetters(null);

        // Assert
        Assert.Contains("Alice Johnson", result);
        Assert.Contains("Bob Smith", result);
        Assert.Contains("Alice's week letter content", result);
        Assert.Contains("Bob's week letter content", result);
    }

    [Fact]
    public void GetWeekLetters_WithMixedWeekLetterAvailability_ReturnsCorrect()
    {
        // Arrange
        var aliceWeekLetter = new JObject
        {
            ["ugebreve"] = new JArray
            {
                new JObject
                {
                    ["klasseNavn"] = "3A",
                    ["uge"] = "42",
                    ["indhold"] = "Alice has a week letter"
                }
            }
        };

        _mockDataManager.Setup(d => d.GetWeekLetter(It.Is<Child>(c => c.FirstName == "Alice"), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(aliceWeekLetter);
        _mockDataManager.Setup(d => d.GetWeekLetter(It.Is<Child>(c => c.FirstName == "Bob"), It.IsAny<int>(), It.IsAny<int>()))
            .Returns((JObject?)null);

        // Act
        var result = _aiToolsManager.GetWeekLetters(null);

        // Assert
        Assert.Contains("Alice Johnson", result);
        Assert.Contains("Bob Smith", result);
        Assert.Contains("Alice has a week letter", result);
        Assert.Contains("No week letter available", result);
    }

    private static JObject CreateTestWeekLetterWithActivities()
    {
        return JObject.Parse(@"
		{
			""ugebreve"": [
				{
					""klasseNavn"": ""3A"",
					""uge"": ""3"",
					""indhold"": ""<p>Weekly activities:</p>
						<p><strong>Monday:</strong> Math lesson on fractions, Science project presentations</p>
						<p><strong>Tuesday:</strong> Art class - painting landscapes</p>
						<p><strong>Wednesday:</strong> PE - indoor games due to weather</p>
						<p><strong>Thursday:</strong> Danish literature reading</p>
						<p><strong>Friday:</strong> Quiz day and free reading time</p>
						<p>Have a great week!</p>""
				}
			]
		}");
    }
}
