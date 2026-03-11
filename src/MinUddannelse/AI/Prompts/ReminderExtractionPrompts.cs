namespace MinUddannelse.AI.Prompts;
using System.Globalization;
using MinUddannelse.AI.Prompts;
using MinUddannelse.Models;
using MinUddannelse.Repositories.DTOs;

public static class ReminderExtractionPrompts
{
    public static string GetExtractionPrompt(string query, DateTime currentTime, string defaultReminderTime = "06:45")
    {
        var nextMonday = GetNextWeekday(currentTime, DayOfWeek.Monday);
        var nextTuesday = GetNextWeekday(currentTime, DayOfWeek.Tuesday);
        var nextWednesday = GetNextWeekday(currentTime, DayOfWeek.Wednesday);
        var nextThursday = GetNextWeekday(currentTime, DayOfWeek.Thursday);
        var nextFriday = GetNextWeekday(currentTime, DayOfWeek.Friday);
        var nextSaturday = GetNextWeekday(currentTime, DayOfWeek.Saturday);
        var nextSunday = GetNextWeekday(currentTime, DayOfWeek.Sunday);

        return $@"You must respond with ONLY valid JSON. No explanations, no markdown, no text outside the JSON.

Extract reminder details from this natural language request. The user may request MULTIPLE reminders in a single message (e.g. listing several dates). Create a SEPARATE reminder for each date mentioned.

Query: ""{query}""

CHILD NAME EXTRACTION:
If the query starts with ""[Context: Child X]"", extract X as the child name.
Example: ""[Context: Child Hans]"" means CHILD: Hans

RECURRING PATTERN DETECTION (ONLY these patterns mean recurring):
Danish: ""hver dag""/""dagligt"" = daily, ""hver mandag"" = weekly Monday, ""hver tirsdag"" = weekly Tuesday, ""hver onsdag"" = weekly Wednesday, ""hver torsdag"" = weekly Thursday, ""hver fredag"" = weekly Friday, ""hver lørdag"" = weekly Saturday, ""hver søndag"" = weekly Sunday
English: ""every day""/""daily"" = daily, ""every Monday"" = weekly Monday, ""weekly"" = weekly

ONE-TIME REMINDERS (NOT recurring - these are for specific dates only):
Current time is {currentTime:yyyy-MM-dd HH:mm}
- ""tomorrow"" / ""i morgen"" = {currentTime.Date.AddDays(1):yyyy-MM-dd}
- ""today"" / ""i dag"" = {currentTime.Date:yyyy-MM-dd}
- ""mandag"" (without ""hver"") = {nextMonday:yyyy-MM-dd} (one-time, NOT recurring)
- ""tirsdag"" (without ""hver"") = {nextTuesday:yyyy-MM-dd} (one-time, NOT recurring)
- ""onsdag"" (without ""hver"") = {nextWednesday:yyyy-MM-dd} (one-time, NOT recurring)
- ""torsdag"" (without ""hver"") = {nextThursday:yyyy-MM-dd} (one-time, NOT recurring)
- ""fredag"" (without ""hver"") = {nextFriday:yyyy-MM-dd} (one-time, NOT recurring)
- ""lørdag"" (without ""hver"") = {nextSaturday:yyyy-MM-dd} (one-time, NOT recurring)
- ""søndag"" (without ""hver"") = {nextSunday:yyyy-MM-dd} (one-time, NOT recurring)
- ""in 2 hours"" / ""om 2 timer"" = {currentTime.AddHours(2):yyyy-MM-dd HH:mm}
- ""om 30 minutter"" = {currentTime.AddMinutes(30):yyyy-MM-dd HH:mm}
- Specific dates like ""d. 3. maj"", ""18 maj"", ""25 maj"" = resolve to actual yyyy-MM-dd dates

CRITICAL RULES:
- A day name WITHOUT ""hver""/""every"" is ONE-TIME, not recurring
- When multiple dates are listed (e.g. ""3. maj, 18 maj og 25 maj""), create a SEPARATE reminder for EACH date
- ""onsdag d. 3. maj, 18 maj og 25 maj"" = THREE one-time reminders, NOT one recurring reminder

REMINDER TIME RULES:
- The datetime field is WHEN TO SEND THE REMINDER, not when the activity happens
- Default reminder time is {defaultReminderTime} — use this unless the user explicitly says ""kl."", ""at"", or ""klokken"" followed by a time
- Times in the reminder TEXT (e.g. ""8-10"", ""kl. 9"") are ACTIVITY times — keep them in the description but do NOT use them as the reminder time
- Example: ""Husk Mesterdetektiver i dag 8-10"" on March 11 → datetime=""2026-03-11 {defaultReminderTime}"", description=""Husk Mesterdetektiver i dag 8-10""
- Example: ""påmind mig kl. 14 om at hente pakke"" → datetime uses 14:00 because the user explicitly requested that time

Return a JSON array of reminders:
[
  {{
    ""description"": ""What to remind about"",
    ""datetime"": ""yyyy-MM-dd HH:mm"",
    ""child"": ""child name or null"",
    ""is_recurring"": false,
    ""recurrence_type"": null,
    ""day_of_week"": null
  }}
]

For recurring reminders, set is_recurring=true, recurrence_type=""daily""/""weekly"", day_of_week=0-6 (0=Sunday).
Response must be valid JSON only.";
    }

    private static DateTime GetNextWeekday(DateTime currentTime, DayOfWeek targetDay)
    {
        var daysUntilTarget = ((int)targetDay - (int)currentTime.DayOfWeek + 7) % 7;
        if (daysUntilTarget == 0 && currentTime.TimeOfDay > TimeSpan.FromHours(12))
        {
            daysUntilTarget = 7; // If it's past noon on the target day, go to next week
        }
        return currentTime.Date.AddDays(daysUntilTarget == 0 ? 7 : daysUntilTarget);
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

ACTIONABILITY FILTERING:

✅ CREATE REMINDERS FOR:
- Required supplies/materials needed on specific day (""medbring godt tøj onsdag"", ""tag madpakke med torsdag"")
- Tests, exams with specific preparation needed (""staveprøve fredag - øv staveord"")
- Field trips, excursions, outings (""tur"", ""udflugt"", ""ekskursion"") - these ALWAYS require preparation (appropriate clothing, lunch, etc.) even if the letter doesn't explicitly list what to bring
- Special school days or events that break the normal routine (""temadag"", ""motionsdag"", ""skolefest"")
- Deadlines for parents (""aflever"", ""tilmelding inden"", ""svar senest"")

❌ EXCLUDE:
- New students, staff, or people joining (""ny elev"", ""ny lærer"")
- Future events more than 2 weeks away with no immediate action needed
- Meetings, conferences (""skole-hjem samtaler"", ""forældremøde"") - these are calendar events
- ""More info coming"" events with no actionable details yet
- Curriculum information (""vi arbejder med"", ""vi fortsætter"")
- Events where parents are passive recipients of information only

IMPORTANT: When in doubt about whether an event is actionable, CREATE the reminder. It is better to remind about something that turns out to be unimportant than to miss a trip or deadline.

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
Only include events with confidence >= 0.80.
If no actionable events found, return: {{""this_week"": [], ""future"": []}}

Response must be valid JSON only.";
    }
}
