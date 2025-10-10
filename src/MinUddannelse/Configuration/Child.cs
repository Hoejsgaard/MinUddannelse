namespace MinUddannelse.Configuration;

public class Child
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Colour { get; set; } = string.Empty;
    public UniLogin? UniLogin { get; set; }
    public ChildChannels? Channels { get; set; }

    public string GetChildId() => GenerateChildId(FirstName);

    public static string GenerateChildId(string firstName) =>
        firstName.ToLowerInvariant().Replace(" ", "_");
}

public class ChildChannels
{
    public ChildSlackConfig? Slack { get; set; }
    public ChildTelegramConfig? Telegram { get; set; }
    public ChildGoogleCalendarConfig? GoogleCalendar { get; set; }
}

public class ChildSlackConfig
{
    public bool Enabled { get; set; }
    public string? WebhookUrl { get; set; }
    public string? ApiToken { get; set; }
    public string? ChannelId { get; set; }
    public bool EnableBot { get; set; }
    public bool EnableInteractive { get; set; } = true;
    public bool SendStartupMessage { get; set; } = true;
    public int PollingIntervalSeconds { get; set; } = 5;
    public int CleanupIntervalHours { get; set; } = 1;
}

public class ChildTelegramConfig
{
    public bool Enabled { get; set; }
    public string? Token { get; set; }
    public long? ChatId { get; set; }
    public bool EnableBot { get; set; }
    public bool EnableInteractive { get; set; } = true;
    public bool SendStartupMessage { get; set; } = true;
}

public class ChildGoogleCalendarConfig
{
    public bool Enabled { get; set; }
    public string? CalendarId { get; set; }
}
