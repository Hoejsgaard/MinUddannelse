using Microsoft.Extensions.Logging;
using MinUddannelse.AI.Services;
using MinUddannelse.Repositories;
using Moq;
using Newtonsoft.Json.Linq;
using System.Reflection;
using Xunit;
using System.Text;

namespace MinUddannelse.Tests.AI.Services;

/// <summary>
/// Integration tests for WeekLetterReminderService that make actual OpenAI API calls.
///
/// IMPORTANT: These tests cost real money as they call the OpenAI API.
/// They are not executed automatically and must be run manually.
/// They DO NOT create actual reminders - only test AI categorization logic.
///
/// To run these tests manually:
/// dotnet test --filter "Category=Integration&Category=Manual"
///
/// Requires valid OpenAI API key in environment variable OPENAI_API_KEY
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Manual")]
public class WeekLetterReminderIntegrationTests
{
    private readonly Mock<ILogger<WeekLetterReminderService>> _mockLogger;
    private readonly Mock<IWeekLetterRepository> _mockWeekLetterRepository;
    private readonly Mock<IReminderRepository> _mockReminderRepository;
    private readonly Mock<IOpenAiService>? _mockOpenAiService;
    private readonly WeekLetterReminderService? _service;

    public WeekLetterReminderIntegrationTests()
    {
        _mockLogger = new Mock<ILogger<WeekLetterReminderService>>();
        _mockWeekLetterRepository = new Mock<IWeekLetterRepository>();
        _mockReminderRepository = new Mock<IReminderRepository>();

        // Get API key from environment - skip tests if not available
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;

        if (string.IsNullOrEmpty(apiKey))
        {
            // Don't initialize service if no API key
            _service = null;
            return;
        }

        // Create a mock that will make real API calls for CreateCompletionAsync only
        _mockOpenAiService = new Mock<IOpenAiService>();

        // Setup the real OpenAI service for completion calls
        var realOpenAiService = CreateRealOpenAiService(apiKey);
        _mockOpenAiService!.Setup(x => x.CreateCompletionAsync(It.IsAny<string>(), It.IsAny<string>()))
                          .Returns<string, string>((prompt, model) => realOpenAiService.CreateCompletionAsync(prompt, model));

        _service = new WeekLetterReminderService(
            _mockOpenAiService!.Object,
            _mockLogger.Object,
            _mockWeekLetterRepository.Object,
            _mockReminderRepository.Object,
            "gpt-4o-mini",
            TimeOnly.Parse("06:45"));
    }

    private static IOpenAiService CreateRealOpenAiService(string apiKey)
    {
        // Create a minimal implementation that only supports CreateCompletionAsync
        return new TestOpenAiService(apiKey);
    }

    private sealed class TestOpenAiService : IOpenAiService
    {
        private readonly string _apiKey;

        public TestOpenAiService(string apiKey)
        {
            _apiKey = apiKey;
        }

        public async Task<CompletionResult> CreateCompletionAsync(string prompt, string model)
        {
            try
            {
                // Make actual OpenAI API call using HttpClient directly
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");

                var requestBody = new
                {
                    model = model,
                    prompt = prompt,
                    max_tokens = 2000,
                    temperature = 0.7
                };

                var json = System.Text.Json.JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync("https://api.openai.com/v1/completions", content);

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    var jsonDocument = System.Text.Json.JsonDocument.Parse(responseContent);
                    var text = jsonDocument.RootElement
                        .GetProperty("choices")[0]
                        .GetProperty("text")
                        .GetString() ?? string.Empty;

                    return new CompletionResult
                    {
                        IsSuccess = true,
                        Content = text
                    };
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    return new CompletionResult
                    {
                        IsSuccess = false,
                        ErrorMessage = $"HTTP {response.StatusCode}: {errorContent}"
                    };
                }
            }
            catch (Exception ex)
            {
                return new CompletionResult
                {
                    IsSuccess = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        public Task<string?> GetResponseAsync(MinUddannelse.Configuration.Child child, string query)
        {
            throw new NotImplementedException("Not needed for integration tests");
        }

        public Task<string?> GetResponseWithContextAsync(MinUddannelse.Configuration.Child child, string query, string conversationId)
        {
            throw new NotImplementedException("Not needed for integration tests");
        }

        public Task ClearConversationHistoryAsync(MinUddannelse.Configuration.Child child, string conversationId)
        {
            throw new NotImplementedException("Not needed for integration tests");
        }
    }

    [Fact]
    public async Task ExtractEventsFromWeekLetter_WithSorenExample_CorrectlyCategorizesEvents()
    {
        // Skip test if no API key is available
        if (_service == null)
        {
            Assert.True(true, "Skipped: OpenAI API key not found in environment variable OPENAI_API_KEY");
            return;
        }
        // Arrange - Based on real Søren newsletter that had categorization issues
        var weekLetterContent = @"
        Kære forældre til 3. klasse

        Denne uge:
        Onsdag: Vi har besøg af tandplejen kl. 10-12. Børnene skal huske tandbørste.

        Fredag: Staveprøve i dansk. Øv hjemmefra med ordene fra liste 7.

        Kommende aktiviteter:
        Tirsdag den 11. november: Skole-hjem samtaler kl. 15-19. Tilmelding sker via Aula.

        5. og 6. november: Overnatning i Ganløsehytten. Børnene skal have sovepose og varmt tøj med.

        Information: Fra uge 45 starter vi nyt tema om vikinger. Mere information følger.
        ";

        // Act - Make actual API call
        var result = await CallExtractEventsFromWeekLetterAsync(weekLetterContent);

        // Assert - Verify temporal categorization
        Assert.NotEmpty(result);

        // Should have current week events
        var currentWeekEvents = result.Where(e => e.IsCurrentWeek).ToList();
        Assert.NotEmpty(currentWeekEvents);

        // Current week events should have day names and be this week's dates
        var wednesdayEvent = currentWeekEvents.FirstOrDefault(e => e.DayOfWeek?.ToLower().Contains("onsdag") == true);
        Assert.NotNull(wednesdayEvent);
        Assert.Contains("tandplejen", wednesdayEvent.Description.ToLower());
        Assert.True(wednesdayEvent.IsCurrentWeek);

        var fridayEvent = currentWeekEvents.FirstOrDefault(e => e.DayOfWeek?.ToLower().Contains("fredag") == true);
        Assert.NotNull(fridayEvent);
        Assert.Contains("staveprøve", fridayEvent.Description.ToLower());
        Assert.True(fridayEvent.IsCurrentWeek);

        // Should have future events with specific dates
        var futureEvents = result.Where(e => !e.IsCurrentWeek).ToList();
        Assert.NotEmpty(futureEvents);

        // November 11th event should be future, not current week
        var novemberEvent = futureEvents.FirstOrDefault(e => e.EventDate.Month == 11 && e.EventDate.Day == 11);
        Assert.NotNull(novemberEvent);
        Assert.Contains("skole-hjem", novemberEvent.Description.ToLower());
        Assert.False(novemberEvent.IsCurrentWeek);
        Assert.Null(novemberEvent.DayOfWeek); // Future events don't have day names

        // Overnight trip should be future event with start date
        var overnightEvent = futureEvents.FirstOrDefault(e => e.EventDate.Month == 11 && e.EventDate.Day == 5);
        Assert.NotNull(overnightEvent);
        Assert.Contains("overnatning", overnightEvent.Description.ToLower());
        Assert.Contains("5. og 6. november", overnightEvent.Description); // Should include full date range
        Assert.False(overnightEvent.IsCurrentWeek);

        // Information-only content should be excluded
        var informationEvents = result.Where(e => e.Description.ToLower().Contains("uge 45") || e.Description.ToLower().Contains("vikinger"));
        Assert.Empty(informationEvents); // Should be filtered out as non-actionable
    }

    [Fact]
    public async Task ExtractEventsFromWeekLetter_WithHansExample_FiltersNonActionableContent()
    {
        // Skip test if no API key is available
        if (_service == null)
        {
            Assert.True(true, "Skipped: OpenAI API key not found in environment variable OPENAI_API_KEY");
            return;
        }

        // Arrange - Based on real Hans newsletter with non-actionable content
        var weekLetterContent = @"
        Uge 43 - 4. klasse

        I denne uge arbejder vi med matematik og dansk som sædvanligt.

        Fredag: Aflevering af projekter om dinosaurer. Husk at tage dem med hjemmefra.

        Fremtidige aktiviteter:
        Tilmelding til vinterophold åbner snart - mere information kommer i næste uge.

        Den 20. november kl. 14: Børnene skal have computere med til IT-undervisning.

        Forældremøde omkring skolefritidsordning. Dato fastlægges senere.

        Julefrokost for forældre - booking åbner torsdag den 24. oktober kl. 12:00.
        ";

        // Act - Make actual API call
        var result = await CallExtractEventsFromWeekLetterAsync(weekLetterContent);

        // Assert - Verify actionability filtering
        Assert.NotEmpty(result);

        // Should include specific actionable items
        var fridayProject = result.FirstOrDefault(e => e.Description.ToLower().Contains("dinosaurer"));
        Assert.NotNull(fridayProject);
        Assert.True(fridayProject.IsCurrentWeek);

        var computerEvent = result.FirstOrDefault(e => e.Description.ToLower().Contains("computere"));
        Assert.NotNull(computerEvent);
        Assert.Equal(11, computerEvent.EventDate.Month);
        Assert.Equal(20, computerEvent.EventDate.Day);
        Assert.False(computerEvent.IsCurrentWeek);

        var bookingEvent = result.FirstOrDefault(e => e.Description.ToLower().Contains("booking"));
        Assert.NotNull(bookingEvent);
        Assert.Equal(10, bookingEvent.EventDate.Month);
        Assert.Equal(24, bookingEvent.EventDate.Day);
        Assert.False(bookingEvent.IsCurrentWeek);

        // Should exclude vague/non-actionable items
        var vagueTilmelding = result.Where(e => e.Description.ToLower().Contains("tilmelding") &&
                                               e.Description.ToLower().Contains("snart"));
        Assert.Empty(vagueTilmelding); // "Tilmelding åbner snart" should be filtered out

        var vagueForældremøde = result.Where(e => e.Description.ToLower().Contains("forældremøde") &&
                                                  e.Description.ToLower().Contains("senere"));
        Assert.Empty(vagueForældremøde); // "Dato fastlægges senere" should be filtered out

        // Should exclude regular curriculum activities
        var regularMatematik = result.Where(e => e.Description.ToLower().Contains("matematik") &&
                                                 e.Description.ToLower().Contains("sædvanligt"));
        Assert.Empty(regularMatematik); // Regular subjects should be filtered out
    }

    [Fact]
    public async Task ExtractEventsFromWeekLetter_WithMixedConfidenceEvents_FiltersLowConfidence()
    {
        // Skip test if no API key is available
        if (_service == null)
        {
            Assert.True(true, "Skipped: OpenAI API key not found in environment variable OPENAI_API_KEY");
            return;
        }

        // Arrange - Content designed to test confidence filtering
        var weekLetterContent = @"
        Påmindelser for uge 43:

        Torsdag: Skolefoto kl. 9-11. Børnene skal være pænt klædt.

        Muligvis fredag: Evt. udflugt til zoo, hvis vejret tillader det.

        10. november: Forældremøde i aula kl. 19:00. Obligatorisk deltagelse.

        Der tales om en juleopførelse i december, men intet er besluttet endnu.
        ";

        // Act - Make actual API call
        var result = await CallExtractEventsFromWeekLetterAsync(weekLetterContent);

        // Assert - Verify confidence filtering
        Assert.NotEmpty(result);

        // High confidence events should be included
        var schoolPhoto = result.FirstOrDefault(e => e.Description.ToLower().Contains("skolefoto"));
        Assert.NotNull(schoolPhoto);
        Assert.True(schoolPhoto.ConfidenceScore >= 0.8);

        var parentMeeting = result.FirstOrDefault(e => e.Description.ToLower().Contains("forældremøde"));
        Assert.NotNull(parentMeeting);
        Assert.True(parentMeeting.ConfidenceScore >= 0.8);

        // Low confidence/uncertain events should be filtered out
        var uncertainZoo = result.Where(e => e.Description.ToLower().Contains("zoo") ||
                                             e.Description.ToLower().Contains("muligvis"));
        Assert.Empty(uncertainZoo); // Should be filtered due to uncertainty

        var vagueBeskrivelse = result.Where(e => e.Description.ToLower().Contains("juleopførelse") ||
                                                 e.Description.ToLower().Contains("tales om"));
        Assert.Empty(vagueBeskrivelse); // Should be filtered due to vagueness
    }

    [Fact]
    public async Task ExtractEventsFromWeekLetter_WithComplexDateFormats_ParsesCorrectly()
    {
        // Skip test if no API key is available
        if (_service == null)
        {
            Assert.True(true, "Skipped: OpenAI API key not found in environment variable OPENAI_API_KEY");
            return;
        }

        // Arrange - Test various Danish date formats
        var weekLetterContent = @"
        Forskellige datoformater:

        Mandag den 21. oktober: Biblioteksbesøg kl. 10.

        Tirsdag 22/10: Gymnastik i hallen.

        Onsdag d. 23. oktober: Madkundskab - børnene skal have forklæde med.

        15. november: Landsholdsdag - alle skal have Danmarkstrøje på.

        Den 3. december kl. 14-16: Julebasar i skolegården.
        ";

        // Act - Make actual API call
        var result = await CallExtractEventsFromWeekLetterAsync(weekLetterContent);

        // Assert - Verify date parsing
        Assert.NotEmpty(result);

        // Current week events (October 21-23, 2025 assuming current week)
        var libraryVisit = result.FirstOrDefault(e => e.Description.ToLower().Contains("bibliotek"));
        Assert.NotNull(libraryVisit);

        var gymnastics = result.FirstOrDefault(e => e.Description.ToLower().Contains("gymnastik"));
        Assert.NotNull(gymnastics);

        var cooking = result.FirstOrDefault(e => e.Description.ToLower().Contains("madkundskab"));
        Assert.NotNull(cooking);

        // Future events with specific dates
        var nationalDay = result.FirstOrDefault(e => e.Description.ToLower().Contains("landsholdsdag"));
        Assert.NotNull(nationalDay);
        Assert.Equal(11, nationalDay.EventDate.Month);
        Assert.Equal(15, nationalDay.EventDate.Day);
        Assert.False(nationalDay.IsCurrentWeek);

        var christmasMarket = result.FirstOrDefault(e => e.Description.ToLower().Contains("julebasar"));
        Assert.NotNull(christmasMarket);
        Assert.Equal(12, christmasMarket.EventDate.Month);
        Assert.Equal(3, christmasMarket.EventDate.Day);
        Assert.False(christmasMarket.IsCurrentWeek);
    }

    /// <summary>
    /// Helper method to call the private ExtractEventsFromWeekLetterAsync method
    /// </summary>
    private async Task<List<ExtractedEvent>> CallExtractEventsFromWeekLetterAsync(string weekLetterContent)
    {
        Assert.NotNull(_service);

        var method = typeof(WeekLetterReminderService).GetMethod("ExtractEventsFromWeekLetterAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        var task = (Task<List<ExtractedEvent>>)method.Invoke(_service, new object[] { weekLetterContent })!;
        return await task;
    }
}
