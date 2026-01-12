using MinUddannelse.Content.WeekLetters;
using MinUddannelse.Models;
using MinUddannelse.Repositories.DTOs;
using MinUddannelse.Security;
using MinUddannelse;
using MinUddannelse.Client;
using MinUddannelse.Configuration;
using MinUddannelse.Repositories;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace MinUddannelse.Content.WeekLetters;

public class WeekLetterService : IWeekLetterService
{
    private readonly IChildAuditService _auditService;
    private readonly IChildRateLimiter _rateLimiter;
    private readonly WeekLetterCache _dataService;
    private readonly IWeekLetterRepository _weekLetterRepository;
    private readonly IMinUddannelseClient _minUddannelseClient;
    private readonly ILogger _logger;

    public WeekLetterService(
        IChildAuditService auditService,
        IChildRateLimiter rateLimiter,
        WeekLetterCache dataService,
        IWeekLetterRepository weekLetterRepository,
        IMinUddannelseClient minUddannelseClient,
        ILoggerFactory loggerFactory)
    {
        _auditService = auditService ?? throw new ArgumentNullException(nameof(auditService));
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _dataService = dataService ?? throw new ArgumentNullException(nameof(dataService));
        _weekLetterRepository = weekLetterRepository ?? throw new ArgumentNullException(nameof(weekLetterRepository));
        _minUddannelseClient = minUddannelseClient ?? throw new ArgumentNullException(nameof(minUddannelseClient));
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger<WeekLetterService>();
    }

    public async Task CacheWeekLetterAsync(Child child, int weekNumber, int year, JObject weekLetter)
    {
        ArgumentNullException.ThrowIfNull(child);

        if (!await _rateLimiter.IsAllowedAsync(child, "CacheWeekLetter"))
        {
            _logger.LogWarning("Rate limit exceeded for {ChildName} caching week letter", child.FirstName);
            await _auditService.LogSecurityEventAsync(child, "RateLimitExceeded", "CacheWeekLetter", SecuritySeverity.Warning);
            throw new RateLimitExceededException("CacheWeekLetter", child.FirstName, 100, TimeSpan.FromMinutes(1));
        }

        try
        {
            _logger.LogInformation("Caching week letter for {ChildName} week {WeekNumber}/{Year}",
                child.FirstName, weekNumber, year);

            _dataService.CacheWeekLetter(child, weekNumber, year, weekLetter);

            await _rateLimiter.RecordOperationAsync(child, "CacheWeekLetter");
            await _auditService.LogDataAccessAsync(child, "CacheWeekLetter", $"week_{weekNumber}_{year}", true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cache week letter for {ChildName}", child.FirstName);
            await _auditService.LogDataAccessAsync(child, "CacheWeekLetter", $"week_{weekNumber}_{year}", false);
            throw;
        }
    }

    public async Task<JObject?> GetWeekLetterAsync(Child child, int weekNumber, int year)
    {
        ArgumentNullException.ThrowIfNull(child);

        if (!await _rateLimiter.IsAllowedAsync(child, "GetWeekLetter"))
        {
            _logger.LogWarning("Rate limit exceeded for {ChildName} reading week letter", child.FirstName);
            await _auditService.LogSecurityEventAsync(child, "RateLimitExceeded", "GetWeekLetter", SecuritySeverity.Warning);
            throw new RateLimitExceededException("GetWeekLetter", child.FirstName, 100, TimeSpan.FromMinutes(1));
        }

        try
        {
            _logger.LogInformation("Retrieving week letter for {ChildName} week {WeekNumber}/{Year}",
                child.FirstName, weekNumber, year);

            var result = _dataService.GetWeekLetter(child, weekNumber, year);

            await _rateLimiter.RecordOperationAsync(child, "GetWeekLetter");
            await _auditService.LogDataAccessAsync(child, "GetWeekLetter", $"week_{weekNumber}_{year}", result != null);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve week letter for {ChildName}", child.FirstName);
            await _auditService.LogDataAccessAsync(child, "GetWeekLetter", $"week_{weekNumber}_{year}", false);
            throw;
        }
    }

    public async Task CacheWeekScheduleAsync(Child child, int weekNumber, int year, JObject weekSchedule)
    {
        ArgumentNullException.ThrowIfNull(child);

        if (!await _rateLimiter.IsAllowedAsync(child, "CacheWeekSchedule"))
        {
            _logger.LogWarning("Rate limit exceeded for {ChildName} caching week schedule", child.FirstName);
            await _auditService.LogSecurityEventAsync(child, "RateLimitExceeded", "CacheWeekSchedule", SecuritySeverity.Warning);
            throw new RateLimitExceededException("CacheWeekSchedule", child.FirstName, 100, TimeSpan.FromMinutes(1));
        }

        try
        {
            _logger.LogInformation("Caching week schedule for {ChildName} week {WeekNumber}/{Year}",
                child.FirstName, weekNumber, year);

            _dataService.CacheWeekSchedule(child, weekNumber, year, weekSchedule);

            await _rateLimiter.RecordOperationAsync(child, "CacheWeekSchedule");
            await _auditService.LogDataAccessAsync(child, "CacheWeekSchedule", $"schedule_{weekNumber}_{year}", true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cache week schedule for {ChildName}", child.FirstName);
            await _auditService.LogDataAccessAsync(child, "CacheWeekSchedule", $"schedule_{weekNumber}_{year}", false);
            throw;
        }
    }

    public async Task<JObject?> GetWeekScheduleAsync(Child child, int weekNumber, int year)
    {
        ArgumentNullException.ThrowIfNull(child);

        if (!await _rateLimiter.IsAllowedAsync(child, "GetWeekSchedule"))
        {
            _logger.LogWarning("Rate limit exceeded for {ChildName} reading week schedule", child.FirstName);
            await _auditService.LogSecurityEventAsync(child, "RateLimitExceeded", "GetWeekSchedule", SecuritySeverity.Warning);
            throw new RateLimitExceededException("GetWeekSchedule", child.FirstName, 100, TimeSpan.FromMinutes(1));
        }

        try
        {
            _logger.LogInformation("Retrieving week schedule for {ChildName} week {WeekNumber}/{Year}",
                child.FirstName, weekNumber, year);

            var result = _dataService.GetWeekSchedule(child, weekNumber, year);

            await _rateLimiter.RecordOperationAsync(child, "GetWeekSchedule");
            await _auditService.LogDataAccessAsync(child, "GetWeekSchedule", $"schedule_{weekNumber}_{year}", result != null);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve week schedule for {ChildName}", child.FirstName);
            await _auditService.LogDataAccessAsync(child, "GetWeekSchedule", $"schedule_{weekNumber}_{year}", false);
            throw;
        }
    }

    public async Task<bool> StoreWeekLetterAsync(Child child, int weekNumber, int year, JObject weekLetter)
    {
        ArgumentNullException.ThrowIfNull(child);

        if (!await _rateLimiter.IsAllowedAsync(child, "StoreWeekLetter"))
        {
            _logger.LogWarning("Rate limit exceeded for {ChildName} storing week letter", child.FirstName);
            await _auditService.LogSecurityEventAsync(child, "RateLimitExceeded", "StoreWeekLetter", SecuritySeverity.Warning);
            throw new RateLimitExceededException("StoreWeekLetter", child.FirstName, 10, TimeSpan.FromMinutes(1));
        }

        try
        {
            _logger.LogInformation("Storing week letter for {ChildName} week {WeekNumber}/{Year} in database",
                child.FirstName, weekNumber, year);

            var contentHash = ComputeHash(weekLetter.ToString());
            await _weekLetterRepository.StoreWeekLetterAsync(
                child.FirstName,
                weekNumber,
                year,
                contentHash,
                weekLetter.ToString());

            await _rateLimiter.RecordOperationAsync(child, "StoreWeekLetter");
            await _auditService.LogDataAccessAsync(child, "StoreWeekLetter", $"db_week_{weekNumber}_{year}", true);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store week letter for {ChildName}", child.FirstName);
            await _auditService.LogDataAccessAsync(child, "StoreWeekLetter", $"db_week_{weekNumber}_{year}", false);
            return false;
        }
    }

    public async Task<bool> DeleteWeekLetterAsync(Child child, int weekNumber, int year)
    {
        ArgumentNullException.ThrowIfNull(child);

        if (!await _rateLimiter.IsAllowedAsync(child, "DeleteWeekLetter"))
        {
            _logger.LogWarning("Rate limit exceeded for {ChildName} deleting week letter", child.FirstName);
            await _auditService.LogSecurityEventAsync(child, "RateLimitExceeded", "DeleteWeekLetter", SecuritySeverity.Warning);
            throw new RateLimitExceededException("DeleteWeekLetter", child.FirstName, 5, TimeSpan.FromMinutes(10));
        }

        try
        {
            _logger.LogWarning("Deleting week letter for {ChildName} week {WeekNumber}/{Year} from database",
                child.FirstName, weekNumber, year);
            await _auditService.LogSecurityEventAsync(child, "DataDeletion", $"week_{weekNumber}_{year}", SecuritySeverity.Warning);

            await _weekLetterRepository.DeleteWeekLetterAsync(child.FirstName, weekNumber, year);

            await _rateLimiter.RecordOperationAsync(child, "DeleteWeekLetter");
            await _auditService.LogDataAccessAsync(child, "DeleteWeekLetter", $"db_week_{weekNumber}_{year}", true);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete week letter for {ChildName}", child.FirstName);
            await _auditService.LogDataAccessAsync(child, "DeleteWeekLetter", $"db_week_{weekNumber}_{year}", false);
            return false;
        }
    }

    public async Task<List<JObject>> GetStoredWeekLettersAsync(Child child, int? year = null)
    {
        ArgumentNullException.ThrowIfNull(child);

        if (!await _rateLimiter.IsAllowedAsync(child, "GetStoredWeekLetters"))
        {
            _logger.LogWarning("Rate limit exceeded for {ChildName} reading stored week letters", child.FirstName);
            await _auditService.LogSecurityEventAsync(child, "RateLimitExceeded", "GetStoredWeekLetters", SecuritySeverity.Warning);
            throw new RateLimitExceededException("GetStoredWeekLetters", child.FirstName, 20, TimeSpan.FromMinutes(1));
        }

        try
        {
            _logger.LogInformation("Retrieving stored week letters for {ChildName} year {Year}",
                child.FirstName, year?.ToString() ?? "all");

            var letters = await _weekLetterRepository.GetStoredWeekLettersAsync(child.FirstName, year);

            var results = new List<JObject>();
            foreach (var letter in letters)
            {
                if (!string.IsNullOrEmpty(letter.RawContent))
                {
                    try
                    {
                        var json = JObject.Parse(letter.RawContent);
                        results.Add(json);
                    }
                    catch (Exception parseEx)
                    {
                        _logger.LogWarning(parseEx, "Failed to parse stored week letter for {ChildName}", child.FirstName);
                    }
                }
            }

            await _rateLimiter.RecordOperationAsync(child, "GetStoredWeekLetters");
            await _auditService.LogDataAccessAsync(child, "GetStoredWeekLetters", $"db_year_{year?.ToString() ?? "all"}", true);

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve stored week letters for {ChildName}", child.FirstName);
            await _auditService.LogDataAccessAsync(child, "GetStoredWeekLetters", $"db_year_{year?.ToString() ?? "all"}", false);
            return new List<JObject>();
        }
    }

    public async Task<JObject?> GetOrFetchWeekLetterAsync(Child child, DateOnly date, bool allowLiveFetch = false)
    {
        ArgumentNullException.ThrowIfNull(child);

        var weekInfo = CalculateWeekInfo(date);
        await ValidateRateLimits(child);

        try
        {
            LogWeekLetterRequest(child, date, weekInfo);

            var cachedResult = CheckMemoryCache(child, weekInfo);
            if (cachedResult != null) return cachedResult;

            var databaseResult = await CheckDatabase(child, weekInfo);
            if (databaseResult != null) return databaseResult;

            var liveResult = allowLiveFetch ? await TryLiveFetch(child, date, weekInfo) : null;
            if (liveResult != null) return liveResult;

            await LogFinalAudit(child, weekInfo, false);
            return null;
        }
        catch (Exception ex)
        {
            await HandleWeekLetterError(child, weekInfo, ex);
            throw;
        }
    }

    private WeekInfo CalculateWeekInfo(DateOnly date)
    {
        var calendar = System.Globalization.CultureInfo.InvariantCulture.Calendar;
        var weekNumber = calendar.GetWeekOfYear(date.ToDateTime(TimeOnly.MinValue),
            System.Globalization.CalendarWeekRule.FirstFourDayWeek,
            DayOfWeek.Monday);

        return new WeekInfo { WeekNumber = weekNumber, Year = date.Year };
    }

    private async Task ValidateRateLimits(Child child)
    {
        if (!await _rateLimiter.IsAllowedAsync(child, "GetOrFetchWeekLetter"))
        {
            _logger.LogWarning("Rate limit exceeded for {ChildName} getting week letter", child.FirstName);
            await _auditService.LogSecurityEventAsync(child, "RateLimitExceeded", "GetOrFetchWeekLetter", SecuritySeverity.Warning);
            throw new RateLimitExceededException("GetOrFetchWeekLetter", child.FirstName, 50, TimeSpan.FromMinutes(1));
        }
    }

    private void LogWeekLetterRequest(Child child, DateOnly date, WeekInfo weekInfo)
    {
        _logger.LogInformation("Getting or fetching week letter for {ChildName} date {Date} (week {WeekNumber}/{Year})",
            child.FirstName, date, weekInfo.WeekNumber, weekInfo.Year);
    }

    private JObject? CheckMemoryCache(Child child, WeekInfo weekInfo)
    {
        var cached = _dataService.GetWeekLetter(child, weekInfo.WeekNumber, weekInfo.Year);
        if (cached != null)
        {
            _logger.LogDebug("Week letter found in cache for {ChildName}", child.FirstName);
        }
        return cached;
    }

    private async Task<JObject?> CheckDatabase(Child child, WeekInfo weekInfo)
    {
        var storedContent = await _weekLetterRepository.GetStoredWeekLetterAsync(child.FirstName, weekInfo.WeekNumber, weekInfo.Year);
        if (string.IsNullOrEmpty(storedContent)) return null;

        try
        {
            var json = JObject.Parse(storedContent);
            _dataService.CacheWeekLetter(child, weekInfo.WeekNumber, weekInfo.Year, json);
            _logger.LogDebug("Week letter found in database for {ChildName}", child.FirstName);
            return json;
        }
        catch (Exception parseEx)
        {
            _logger.LogWarning(parseEx, "Failed to parse stored week letter for {ChildName}", child.FirstName);
            return null;
        }
    }

    private async Task<JObject?> TryLiveFetch(Child child, DateOnly date, WeekInfo weekInfo)
    {
        _logger.LogInformation("Fetching week letter from MinUddannelse for {ChildName}", child.FirstName);

        try
        {
            var fetchedLetter = await _minUddannelseClient.GetWeekLetter(child, date, true);

            if (fetchedLetter != null)
            {
                _logger.LogInformation("Successfully fetched week letter for {ChildName} week {WeekNumber}/{Year}",
                    child.FirstName, weekInfo.WeekNumber, weekInfo.Year);

                if (!IsWeekLetterEffectivelyEmpty(fetchedLetter))
                {
                    await CacheWeekLetterAsync(child, weekInfo.WeekNumber, weekInfo.Year, fetchedLetter);
                    await LogFinalAudit(child, weekInfo, true);
                }
                else
                {
                    _logger.LogInformation("Week letter for {ChildName} week {WeekNumber}/{Year} is effectively empty, not caching",
                        child.FirstName, weekInfo.WeekNumber, weekInfo.Year);
                }

                return fetchedLetter;
            }
        }
        catch (Exception fetchEx)
        {
            _logger.LogError(fetchEx, "Failed to fetch week letter from MinUddannelse for {ChildName}", child.FirstName);
        }

        return null;
    }

    private async Task LogFinalAudit(Child child, WeekInfo weekInfo, bool success)
    {
        await _rateLimiter.RecordOperationAsync(child, "GetOrFetchWeekLetter");
        await _auditService.LogDataAccessAsync(child, "GetOrFetchWeekLetter", $"letter_{weekInfo.WeekNumber}_{weekInfo.Year}", success);
    }

    private async Task HandleWeekLetterError(Child child, WeekInfo weekInfo, Exception ex)
    {
        _logger.LogError(ex, "Failed to get or fetch week letter for {ChildName}", child.FirstName);
        await _auditService.LogDataAccessAsync(child, "GetOrFetchWeekLetter", $"letter_{weekInfo.WeekNumber}_{weekInfo.Year}", false);
    }

    private sealed class WeekInfo
    {
        public int WeekNumber { get; init; }
        public int Year { get; init; }
    }

    public async Task<List<JObject>> GetAllWeekLettersAsync(Child child)
    {
        ArgumentNullException.ThrowIfNull(child);

        if (!await _rateLimiter.IsAllowedAsync(child, "GetAllWeekLetters"))
        {
            _logger.LogWarning("Rate limit exceeded for {ChildName} getting all week letters", child.FirstName);
            await _auditService.LogSecurityEventAsync(child, "RateLimitExceeded", "GetAllWeekLetters", SecuritySeverity.Warning);
            throw new RateLimitExceededException("GetAllWeekLetters", child.FirstName, 10, TimeSpan.FromMinutes(5));
        }

        try
        {
            _logger.LogInformation("Getting all week letters for {ChildName}", child.FirstName);

            var letters = await GetStoredWeekLettersAsync(child);

            await _rateLimiter.RecordOperationAsync(child, "GetAllWeekLetters");
            await _auditService.LogDataAccessAsync(child, "GetAllWeekLetters", "all_letters", true);

            return letters;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get all week letters for {ChildName}", child.FirstName);
            await _auditService.LogDataAccessAsync(child, "GetAllWeekLetters", "all_letters", false);
            return new List<JObject>();
        }
    }

    private static string ComputeHash(string content)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }

    public static bool IsWeekLetterEffectivelyEmpty(JObject? weekLetter)
    {
        if (weekLetter == null)
            return true;

        try
        {
            var ugebreve = weekLetter["ugebreve"];
            if (ugebreve != null)
            {
                foreach (var ugeBrev in ugebreve)
                {
                    var indhold = ugeBrev?["indhold"];
                    if (indhold != null)
                    {
                        string content = indhold.ToString().Trim();
                        if (content.Contains("Der er ikke skrevet nogen ugenoter til denne uge") ||
                            content.Contains("Ingen ugenoter") ||
                            string.IsNullOrWhiteSpace(content))
                        {
                            continue;
                        }
                        return false;
                    }
                }
            }

            return true;
        }
        catch
        {
            return true;
        }
    }
}
