namespace MinUddannelse.AI.Prompts;
using System.Globalization;
using MinUddannelse.AI.Prompts;
using MinUddannelse.Models;
using MinUddannelse.Repositories.DTOs;

public static class ReminderExtractionPrompts
{
    public static string GetExtractionPrompt(string query, DateTime currentTime)
    {
        return $@"Extract reminder details from this natural language request:

Query: ""{query}""

Extract:
1. Description: What to remind about
2. DateTime: When to remind (convert to yyyy-MM-dd HH:mm format)
3. ChildName: If mentioned, the child's name (optional)

For relative dates (current time is {currentTime:yyyy-MM-dd HH:mm}):
- ""tomorrow"" = {currentTime.Date.AddDays(1):yyyy-MM-dd}
- ""today"" = {currentTime.Date:yyyy-MM-dd}
- ""next Monday"" = calculate the next Monday
- ""in 2 hours"" = {currentTime.AddHours(2):yyyy-MM-dd HH:mm}
- ""om 2 minutter"" = {currentTime.AddMinutes(2):yyyy-MM-dd HH:mm}
- ""om 30 minutter"" = {currentTime.AddMinutes(30):yyyy-MM-dd HH:mm}

Respond in this exact format:
DESCRIPTION: [extracted description]
DATETIME: [yyyy-MM-dd HH:mm]
CHILD: [child name or NONE]";
    }

    public static string GetWeekLetterEventExtractionPrompt(string weekLetterContent, DateTime currentTime)
    {
        var weekNumber = ISOWeek.GetWeekOfYear(currentTime);
        var year = currentTime.Year;

        var currentMonday = currentTime.Date.AddDays(-(int)currentTime.DayOfWeek + (int)DayOfWeek.Monday);
        var currentFriday = currentMonday.AddDays(4);

        return $@"You must respond with ONLY valid JSON. No explanations, no markdown, no text outside the JSON.

Extract ONLY actionable events that require parent/student preparation from this Danish school week letter for week {weekNumber}/{year}.

Current context:
- Today: {currentTime:yyyy-MM-dd} ({currentTime:dddd})
- Current week {weekNumber} spans: {currentMonday:yyyy-MM-dd} to {currentFriday:yyyy-MM-dd}
- Week {weekNumber} days:
  * Mandag: {currentMonday:yyyy-MM-dd}
  * Tirsdag: {currentMonday.AddDays(1):yyyy-MM-dd}
  * Onsdag: {currentMonday.AddDays(2):yyyy-MM-dd}
  * Torsdag: {currentMonday.AddDays(3):yyyy-MM-dd}
  * Fredag: {currentMonday.AddDays(4):yyyy-MM-dd}

Week Letter: ""{weekLetterContent}""

CRITICAL TEMPORAL RULES:
1. **Parse actual dates first** - If you see ""11. november"", ""20/11"", ""5. og 6. november"" - these are ACTUAL DATES, not current week days
2. **Current Week Events**: Only use Monday-Friday if event explicitly happens this week WITHOUT a specific date
3. **Future Events**: Events with specific dates (November, etc.) stay as their actual dates
4. **Information Only**: Items about ""booking will open"", ""more info coming"" are NOT actionable reminders

ENHANCED ACTIONABILITY FILTERING:

✅ IMMEDIATE ACTION REQUIRED (create reminders):
- Tests, exams today/this week (staveprøve, matematik test)
- Photo sessions with specific dates (skolefoto, klassefoto)
- Required supplies/materials needed today/this week (medbring computer, godt tøj)
- Field trips happening this week with preparation needed
- Events requiring presence THIS WEEK

✅ FUTURE ACTION WITH SPECIFIC DATES (create reminders with actual dates):
- Parent meetings with specific November dates (skole-hjem samtaler 11. november)
- Overnight trips with specific dates (overnatning 5. og 6. november)
- Events requiring advance preparation with known dates

❌ EXCLUDE (NOT actionable reminders):
- Information about future booking opportunities WITHOUT specific dates (""tilmelding åbner i uge 43"")
- General curriculum activities (fortsætte med dansk, historie temaer)
- Regular subjects without special preparation (kristendom, almindelig idræt)
- Future events with ""more info coming"" WITHOUT actionable dates
- Activities without clear parent/student action needed
- Vague homework mentions without specific deadlines

✅ BUT DO INCLUDE if booking has specific actionable date:
- ""Tilmelding åbner torsdag"" = CREATE reminder for Thursday
- ""Booking opens on Thursday the 24th"" = CREATE reminder for that specific date

DATE PARSING RULES:
- ""onsdag"" without date = {currentMonday.AddDays(2):yyyy-MM-dd} (current week Wednesday)
- ""onsdag den 22. oktober"" = 2025-10-22 (specific date)
- ""tirsdag 11. november"" = 2025-11-11 (specific date)
- ""5. og 6. november"" = 2025-11-05 (use start date, but include full range in description)
- ""torsdag den 20/11"" = 2025-11-20 (specific date)

DESCRIPTION QUALITY:
- Rich, standalone Danish text with full context
- Include times when mentioned (""kl. 8.00"")
- Include preparation details (""medbring madpakke"", ""godt tøj til vejret"")
- NO generic prefixes like ""Husk:"" or ""Event:""

Return JSON with this structure:
{{
  ""this_week"": [
    {{
      ""day"": ""onsdag"",
      ""title"": ""Kort dansk titel"",
      ""description"": ""Complete actionable reminder in Danish"",
      ""date"": ""yyyy-MM-dd"",
      ""type"": ""event"",
      ""confidence"": 0.9
    }}
  ],
  ""future"": [
    {{
      ""title"": ""Kort dansk titel"",
      ""description"": ""Complete actionable reminder in Danish"",
      ""date"": ""yyyy-MM-dd"",
      ""type"": ""deadline"",
      ""confidence"": 0.9
    }}
  ]
}}

Types: event, deadline, supply_needed, permission_form
Only include events with confidence >= 0.8 (high confidence for actionable items only).
If no actionable events found, return: {{""this_week"": [], ""future"": []}}

Response must be valid JSON only.";
    }
}
