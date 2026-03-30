namespace MinUddannelse.AI.Prompts;

using MinUddannelse.AI.Prompts;

public static class IntentAnalysisPrompts
{
    public const string AnalysisTemplate = @"Analyze this user query and determine what they want to do:

Query: ""{0}""

Determine if this query requires any of these ACTIONS:
- CREATE_REMINDER: User wants to set up a reminder/notification
- LIST_REMINDERS: User wants to see their current reminders
- DELETE_REMINDER: User wants to remove a reminder
- GET_CURRENT_TIME: User wants to know the current date/time
- HELP: User wants help or available commands

If the query requires an ACTION, respond with: TOOL_CALL: [ACTION_NAME]
If it's a question about children's school activities, week letters, or schedules, respond with: INFORMATION_QUERY

{1}

Analyze the query and respond accordingly:";

    public static readonly Dictionary<string, string> ToolExamples = new()
    {
        { "CREATE_REMINDER", "\"Remind me tomorrow at 8 AM about Søren Johannes's soccer\" → TOOL_CALL: CREATE_REMINDER" },
        { "CREATE_REMINDER_DANISH", "\"Kan du minde mig om at hente øl om 2 timer?\" → TOOL_CALL: CREATE_REMINDER" },
        { "LIST_REMINDERS", "\"What are my reminders?\" → TOOL_CALL: LIST_REMINDERS" },
        { "LIST_REMINDERS_DANISH", "\"Vis mine påmindelser\" → TOOL_CALL: LIST_REMINDERS" },
        { "DELETE_REMINDER", "\"Delete reminder 2\" → TOOL_CALL: DELETE_REMINDER" },
        { "GET_CURRENT_TIME", "\"What time is it?\" → TOOL_CALL: GET_CURRENT_TIME" },
        { "GET_CURRENT_TIME_DANISH", "\"Hvad er klokken?\" → TOOL_CALL: GET_CURRENT_TIME" },
        { "INFORMATION_QUERY", "\"What does Emma have today?\" → INFORMATION_QUERY" },
        { "INFORMATION_QUERY_DANISH", "\"Hvad skal Hans Martin i dag?\" → INFORMATION_QUERY" },
        { "WEEK_LETTER_QUERY", "\"Show me this week's letter\" → INFORMATION_QUERY" }
    };

    public static string GetFormattedPrompt(string query)
    {
        var examples = string.Join("\n- ", ToolExamples.Values.Prepend("Examples:"));
        return string.Format(AnalysisTemplate, query, examples);
    }
}
