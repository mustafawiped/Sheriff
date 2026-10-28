namespace Sheriff.Core;

public enum AppSource { StartMenu, Store }
public sealed record AppEntry
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string TargetPathOrAumid { get; init; } = "";
    public string Arguments { get; init; } = "";
    public string ShortcutPath { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Description { get; init; } = "";
    public string IconCachePath { get; init; } = "";
    public string Category { get; init; } = Categories.Other;
    public AppSource Source { get; init; }
    public DateTimeOffset InstalledAt { get; init; } = DateTimeOffset.UtcNow;
    public bool IsNew { get; init; }
}

public static class Categories
{
    public const string Software = "Yazılım", Hardware = "Donanım", Social = "Sosyal Medya", Games = "Oyun", Server = "Sunucu", Other = "Diğer", Junk = "Gereksiz";
    public static readonly string[] Defaults = [Software, Hardware, Social, Games, Server, Other, Junk];
}

public sealed class SheriffConfig
{
    public List<string> Categories { get; set; } = [.. Core.Categories.Defaults];
    // Includes disabled custom categories so they can be enabled again later.
    public List<string> AvailableCategories { get; set; } = [];
    // Category IDs remain stable so changing a label does not affect rules or assignments.
    public Dictionary<string, string> CategoryLabels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> CategoryColors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> CategoryOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> PinnedAppIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> HiddenAppIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Apps matching Sheriff safety rules stay out of the launcher until explicitly enabled here.
    public HashSet<string> AllowedAutoBlockedAppIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Hotkey { get; set; } = "Ctrl+Alt+Space";
    public bool StartWithWindows { get; set; }
    public bool LlmEnabled { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string LlmApiKey { get; set; } = "";
    public string LlmProtectedApiKey { get; set; } = "";
    public string LlmEndpoint { get; set; } = "https://api.openai.com/v1/chat/completions";
    public string LlmModel { get; set; } = "gpt-4.1-mini";
}

public sealed record UsageEvent(string AppId, DateTimeOffset LaunchedAt);
