using MinUddannelse.AI.Prompts;
using Xunit;

namespace MinUddannelse.Tests.AI.Prompts;

public class ReminderExtractionPromptsTests
{
    [Fact]
    public void GetExtractionPrompt_WithValidInput_ReturnsFormattedPrompt()
    {
        // Arrange
        var query = "Remind me tomorrow at 8am to pack lunch";
        var currentTime = new DateTime(2025, 10, 15, 14, 30, 0);

        // Act
        var result = ReminderExtractionPrompts.GetExtractionPrompt(query, currentTime);

        // Assert
        Assert.Contains("Extract reminder details from this natural language request:", result);
        Assert.Contains(query, result);
        Assert.Contains("2025-10-15 14:30", result);
        Assert.Contains("2025-10-16", result); // tomorrow
        Assert.Contains("DESCRIPTION: [extracted description]", result);
        Assert.Contains("DATETIME: [yyyy-MM-dd HH:mm]", result);
        Assert.Contains("CHILD: [child name or NONE]", result);
    }

    [Fact]
    public void GetExtractionPrompt_WithDanishRelativeTime_IncludesDanishExamples()
    {
        // Arrange
        var query = "Husk mig om 30 minutter";
        var currentTime = new DateTime(2025, 10, 15, 14, 30, 0);

        // Act
        var result = ReminderExtractionPrompts.GetExtractionPrompt(query, currentTime);

        // Assert
        Assert.Contains("om 2 minutter", result);
        Assert.Contains("om 30 minutter", result);
        Assert.Contains("2025-10-15 15:00", result); // 30 minutes later
    }

    [Fact]
    public void GetWeekLetterEventExtractionPrompt_WithValidInput_ReturnsFormattedPrompt()
    {
        // Arrange
        var weekLetterContent = "Kære forældre, I morgen har vi udflugt til zoo. Husk madpakke og lette sko.";
        var currentTime = new DateTime(2025, 10, 15, 14, 30, 0);

        // Act
        var result = ReminderExtractionPrompts.GetWeekLetterEventExtractionPrompt(weekLetterContent, currentTime);

        // Assert
        Assert.Contains("You must respond with ONLY valid JSON", result);
        Assert.Contains("Extract ONLY actionable events that require parent/student preparation from this Danish school week letter", result);
        Assert.Contains(weekLetterContent, result);
        Assert.Contains("2025-10-15", result);
        Assert.Contains("Return JSON with this structure:", result);
        Assert.Contains("Types: event, deadline, supply_needed, permission_form", result);
    }

    [Fact]
    public void GetWeekLetterEventExtractionPrompt_ContainsCorrectResponseFormat()
    {
        // Arrange
        var weekLetterContent = "Test content";
        var currentTime = DateTime.Now;

        // Act
        var result = ReminderExtractionPrompts.GetWeekLetterEventExtractionPrompt(weekLetterContent, currentTime);

        // Assert
        Assert.Contains("Return JSON with this structure:", result);
        Assert.Contains("\"type\":", result);
        Assert.Contains("\"title\":", result);
        Assert.Contains("\"description\":", result);
        Assert.Contains("\"date\":", result);
        Assert.Contains("\"confidence\":", result);
        Assert.Contains("If no actionable events found, return: {\"this_week\": [], \"future\": []}", result);
        Assert.Contains("Response must be valid JSON only", result);
    }

    [Fact]
    public void GetWeekLetterEventExtractionPrompt_ContainsInstructions()
    {
        // Arrange
        var weekLetterContent = "Test content";
        var currentTime = DateTime.Now;

        // Act
        var result = ReminderExtractionPrompts.GetWeekLetterEventExtractionPrompt(weekLetterContent, currentTime);

        // Assert
        Assert.Contains("Extract ONLY actionable events", result);
        Assert.Contains("Only include events with confidence >= 0.8", result);
        Assert.Contains("Types: event, deadline, supply_needed, permission_form", result);
        Assert.Contains("You must respond with ONLY valid JSON", result);
        Assert.Contains("No explanations, no markdown", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void GetExtractionPrompt_WithInvalidQuery_StillReturnsValidPrompt(string? query)
    {
        // Arrange
        var currentTime = DateTime.Now;

        // Act
        var result = ReminderExtractionPrompts.GetExtractionPrompt(query ?? string.Empty, currentTime);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result);
        Assert.Contains("Extract reminder details", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void GetWeekLetterEventExtractionPrompt_WithInvalidContent_StillReturnsValidPrompt(string? content)
    {
        // Arrange
        var currentTime = DateTime.Now;

        // Act
        var result = ReminderExtractionPrompts.GetWeekLetterEventExtractionPrompt(content ?? string.Empty, currentTime);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result);
        Assert.Contains("You must respond with ONLY valid JSON", result);
    }

    [Fact]
    public void GetExtractionPrompt_WithSpecialCharacters_HandlesCorrectly()
    {
        // Arrange
        var query = "Remind me about Emma's homework \"Math & Science\" at 6:30 PM";
        var currentTime = DateTime.Now;

        // Act
        var result = ReminderExtractionPrompts.GetExtractionPrompt(query, currentTime);

        // Assert
        Assert.Contains(query, result);
        Assert.Contains("Math & Science", result);
        Assert.DoesNotContain("{{", result); // No unresolved template variables
    }

    [Fact]
    public void GetWeekLetterEventExtractionPrompt_WithSpecialCharacters_HandlesCorrectly()
    {
        // Arrange
        var content = "Kære forældre, børnene skal have \"sportsudstyr\" & madpakke i morgen.";
        var currentTime = DateTime.Now;

        // Act
        var result = ReminderExtractionPrompts.GetWeekLetterEventExtractionPrompt(content, currentTime);

        // Assert
        Assert.Contains(content, result);
        Assert.Contains("sportsudstyr", result);
        Assert.DoesNotContain("{{", result); // No unresolved template variables
    }

    [Fact]
    public void GetWeekLetterEventExtractionPrompt_ContainsNewTemporalFormat()
    {
        // Arrange
        var content = "Test content";
        var currentTime = new DateTime(2025, 10, 20, 14, 30, 0); // Monday

        // Act
        var result = ReminderExtractionPrompts.GetWeekLetterEventExtractionPrompt(content, currentTime);

        // Assert - Check new JSON structure
        Assert.Contains("\"this_week\":", result);
        Assert.Contains("\"future\":", result);
        Assert.Contains("\"day\": \"onsdag\"", result);
        Assert.Contains("If no actionable events found, return: {\"this_week\": [], \"future\": []}", result);
    }

    [Fact]
    public void GetWeekLetterEventExtractionPrompt_ContainsCorrectWeekDates()
    {
        // Arrange
        var content = "Test content";
        var currentTime = new DateTime(2025, 10, 22, 14, 30, 0); // Wednesday Oct 22

        // Act
        var result = ReminderExtractionPrompts.GetWeekLetterEventExtractionPrompt(content, currentTime);

        // Assert - Verify week calculation is correct
        Assert.Contains("Mandag: 2025-10-20", result); // Monday of week 43
        Assert.Contains("Tirsdag: 2025-10-21", result); // Tuesday
        Assert.Contains("Onsdag: 2025-10-22", result); // Wednesday (today)
        Assert.Contains("Torsdag: 2025-10-23", result); // Thursday
        Assert.Contains("Fredag: 2025-10-24", result); // Friday
        Assert.Contains("Current week 43 spans: 2025-10-20 to 2025-10-24", result);
    }

    [Fact]
    public void GetWeekLetterEventExtractionPrompt_ContainsDateParsingRules()
    {
        // Arrange
        var content = "Test content";
        var currentTime = new DateTime(2025, 10, 22, 14, 30, 0); // Wednesday

        // Act
        var result = ReminderExtractionPrompts.GetWeekLetterEventExtractionPrompt(content, currentTime);

        // Assert - Check date parsing examples
        Assert.Contains("\"onsdag\" without date = 2025-10-22 (current week Wednesday)", result);
        Assert.Contains("\"onsdag den 22. oktober\" = 2025-10-22 (specific date)", result);
        Assert.Contains("\"tirsdag 11. november\" = 2025-11-11 (specific date)", result);
        Assert.Contains("\"5. og 6. november\" = 2025-11-05 (use start date, but include full range in description)", result);
        Assert.Contains("\"torsdag den 20/11\" = 2025-11-20 (specific date)", result);
    }

    [Fact]
    public void GetWeekLetterEventExtractionPrompt_ContainsEnhancedActionabilityRules()
    {
        // Arrange
        var content = "Test content";
        var currentTime = DateTime.Now;

        // Act
        var result = ReminderExtractionPrompts.GetWeekLetterEventExtractionPrompt(content, currentTime);

        // Assert - Check enhanced filtering rules
        Assert.Contains("Information about future booking opportunities WITHOUT specific dates", result);
        Assert.Contains("Future events with \"more info coming\" WITHOUT actionable dates", result);
        Assert.Contains("BUT DO INCLUDE if booking has specific actionable date:", result);
        Assert.Contains("\"Tilmelding åbner torsdag\" = CREATE reminder for Thursday", result);
        Assert.Contains("\"Booking opens on Thursday the 24th\" = CREATE reminder for that specific date", result);
    }

    [Theory]
    [InlineData(2025, 10, 20)] // Monday
    [InlineData(2025, 10, 21)] // Tuesday
    [InlineData(2025, 10, 22)] // Wednesday
    [InlineData(2025, 10, 23)] // Thursday
    [InlineData(2025, 10, 24)] // Friday
    public void GetWeekLetterEventExtractionPrompt_CalculatesCorrectWeekDatesForAllDays(int year, int month, int day)
    {
        // Arrange
        var content = "Test content";
        var currentTime = new DateTime(year, month, day, 14, 30, 0);

        // Act
        var result = ReminderExtractionPrompts.GetWeekLetterEventExtractionPrompt(content, currentTime);

        // Assert - All days in week 43 should have consistent Monday-Friday dates
        Assert.Contains("Mandag: 2025-10-20", result);
        Assert.Contains("Tirsdag: 2025-10-21", result);
        Assert.Contains("Onsdag: 2025-10-22", result);
        Assert.Contains("Torsdag: 2025-10-23", result);
        Assert.Contains("Fredag: 2025-10-24", result);
    }

    [Fact]
    public void GetWeekLetterEventExtractionPrompt_HandlesWeekend()
    {
        // Arrange
        var content = "Test content";
        var currentTime = new DateTime(2025, 10, 19, 14, 30, 0); // Sunday

        // Act
        var result = ReminderExtractionPrompts.GetWeekLetterEventExtractionPrompt(content, currentTime);

        // Assert - Sunday should still calculate Monday correctly
        Assert.Contains("Mandag: 2025-10-20", result);
        // Check for week number (October 19, 2025 is in week 42)
        Assert.Contains("week 42", result);
    }
}
