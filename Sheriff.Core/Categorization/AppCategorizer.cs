using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sheriff.Core.Categorization;

public sealed record KnownAppRule
{
    public string Name { get; init; } = "";
    public string Category { get; init; } = Categories.Other;
    public string[] ExeNames { get; init; } = [];
    public string[] Aumids { get; init; } = [];
    public string[] Publishers { get; init; } = [];
}

public sealed record CategorizationResult(string Category, bool IsUnclassified, string Reason);

/// <summary>Pure, deterministic categorization. No network or file access occurs during Classify.</summary>
public sealed class AppCategorizer
{
    private readonly KnownAppRule[] _known;
    private readonly Dictionary<string, List<KnownAppRule>> _executables, _aumids, _publishers;
    private static readonly Regex JunkNames = new(@"\b(uninstall(?:er)?|unins\d*|kaldir|readme|help|yardim|release[\s_-]+notes|updater|redistributable|licen[cs]e|documentation)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Tokens = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly (string Category, string[] Publishers, string[] Folders)[] Heuristics =
    [
        (Categories.Hardware, ["nvidia", "logitech", "hyperx", "advanced micro devices", "amd", "intel", "corsair", "razer", "steelseries", "cooler master", "elgato"], ["nvidia corporation", "logitech", "hyperx", "steelseries", "corsair", "razer"]),
        (Categories.Games, ["valve", "epic games", "riot games", "blizzard", "electronic arts", "ubisoft", "gog.com"], ["steamapps", "epic games", "riot games", "battle.net", "ubisoft game launcher", "gog galaxy"]),
        (Categories.Social, ["discord", "telegram", "whatsapp", "slack technologies", "zoom video", "spotify"], ["discord", "telegram desktop", "whatsapp"]),
        (Categories.Server, ["nginx", "mariadb", "mongodb", "postgresql", "redis"], ["postgresql", "mariadb", "mysql", "mongodb", "nginx", "apache group"]),
        (Categories.Software, ["jetbrains", "github", "gitlab", "docker", "postman"], ["jetbrains", "microsoft visual studio", "git", "windows terminal", "powershell"])
    ];
    private static readonly (string Category, Dictionary<string, int> Words)[] Keywords =
    [
        (Categories.Software, new() { ["git"] = 3, ["terminal"] = 3, ["powershell"] = 3, ["sdk"] = 3, ["compiler"] = 3, ["developer"] = 3, ["development"] = 3, ["ide"] = 3, ["editor"] = 2, ["debugger"] = 3, ["debug"] = 2, ["code"] = 1, ["tool"] = 1, ["yazilim"] = 2 }),
        (Categories.Hardware, new() { ["driver"] = 3, ["drivers"] = 3, ["surucu"] = 3, ["firmware"] = 3, ["device"] = 2, ["peripheral"] = 2, ["keyboard"] = 2, ["mouse"] = 2, ["rgb"] = 2, ["donanim"] = 2 }),
        (Categories.Social, new() { ["chat"] = 2, ["messaging"] = 2, ["messenger"] = 3, ["social"] = 2, ["meeting"] = 2, ["sohbet"] = 2, ["voice"] = 1, ["call"] = 1 }),
        (Categories.Games, new() { ["game"] = 2, ["games"] = 2, ["gaming"] = 2, ["oyun"] = 2, ["steam"] = 3, ["launcher"] = 1 }),
        (Categories.Server, new() { ["server"] = 3, ["sunucu"] = 3, ["database"] = 3, ["hosting"] = 3, ["postgres"] = 3, ["mysql"] = 3, ["redis"] = 3, ["apache"] = 3, ["nginx"] = 3 })
    ];

    public AppCategorizer(IEnumerable<KnownAppRule>? rules = null)
    {
        _known = (rules ?? LoadKnownApps()).ToArray();
        _executables = BuildIndex(_known, rule => rule.ExeNames);
        _aumids = BuildIndex(_known, rule => rule.Aumids);
        _publishers = BuildIndex(_known, rule => rule.Publishers);
    }

    // Keep all matches in rule order: a disabled category must not hide a later match.
    private static Dictionary<string, List<KnownAppRule>> BuildIndex(IEnumerable<KnownAppRule> rules, Func<KnownAppRule, string[]?> keys)
    {
        var index = new Dictionary<string, List<KnownAppRule>>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
            foreach (var key in keys(rule) ?? [])
            {
                if (key is null) continue;
                var normalized = key.Trim();
                if (!index.TryGetValue(normalized, out var matches)) index[normalized] = matches = [];
                matches.Add(rule);
            }
        return index;
    }
    public IReadOnlyList<KnownAppRule> KnownApps => _known;
    public string Categorize(AppEntry app, SheriffConfig config) => Classify(app, config).Category;

    public CategorizationResult Classify(AppEntry app, SheriffConfig config)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(config);
        var allowed = new HashSet<string>(config.Categories ?? [], StringComparer.OrdinalIgnoreCase) { Categories.Other, Categories.Junk };
        string Canonical(string category) => (config.Categories ?? []).FirstOrDefault(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase)) ?? category;
        CategorizationResult Match(string category, string reason) => new(Canonical(category), false, reason);

        if (config.CategoryOverrides is not null && config.CategoryOverrides.TryGetValue(app.Id, out var overridden))
            return allowed.Contains(overridden) ? Match(overridden, "user-override") : new(Categories.Other, false, "user-override-category-missing");

        if (IsJunk(app)) return Match(Categories.Junk, "junk-name");
        var executable = ExecutableName(app.TargetPathOrAumid);
        var index = app.Source == AppSource.Store ? _aumids : _executables;
        var identity = app.Source == AppSource.Store ? app.TargetPathOrAumid?.Trim() : executable;
        if ((app.Source == AppSource.Store || app.Source == AppSource.StartMenu) && identity is not null && index.TryGetValue(identity, out var identityMatches))
            foreach (var rule in identityMatches)
                if (allowed.Contains(rule.Category)) return Match(rule.Category, (app.Source == AppSource.Store ? "known-aumid:" : "known-exe:") + rule.Name);
        if (!string.IsNullOrWhiteSpace(app.Publisher) && _publishers.TryGetValue(app.Publisher.Trim(), out var publisherMatches))
            foreach (var rule in publisherMatches)
                if (allowed.Contains(rule.Category)) return Match(rule.Category, "known-publisher:" + rule.Name);

        var publisher = Normalize(app.Publisher ?? "");
        var folders = Normalize(ParentPath(app.TargetPathOrAumid) + " " + ParentPath(app.ShortcutPath));
        foreach (var rule in Heuristics)
        {
            if (!allowed.Contains(rule.Category)) continue;
            if (rule.Publishers.Any(p => HasPhrase(publisher, p))) return Match(rule.Category, "publisher-rule");
            if (rule.Folders.Any(f => HasPhrase(folders, f))) return Match(rule.Category, "folder-rule");
        }

        var words = Tokens.Matches(Normalize((app.DisplayName ?? "") + " " + (app.Description ?? ""))).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        var bestScore = 1;
        string? bestCategory = null;
        foreach (var rule in Keywords)
        {
            if (!allowed.Contains(rule.Category)) continue;
            var score = rule.Words.Where(pair => words.Contains(pair.Key)).Sum(pair => pair.Value);
            if (score > bestScore) { bestScore = score; bestCategory = rule.Category; }
        }
        return bestCategory is null ? new(Categories.Other, true, "unclassified") : Match(bestCategory, "keyword-score:" + bestScore);
    }

    public static bool IsJunk(AppEntry app) => JunkNames.IsMatch(Normalize(app.DisplayName ?? "")) || JunkNames.IsMatch(Normalize(Path.GetFileNameWithoutExtension(ExecutableName(app.TargetPathOrAumid))));

    public static IReadOnlyList<KnownAppRule> LoadKnownApps()
    {
        var assembly = typeof(AppCategorizer).Assembly;
        var resource = assembly.GetManifestResourceNames().SingleOrDefault(n => n.EndsWith("known_apps.json", StringComparison.OrdinalIgnoreCase));
        if (resource is null) return [];
        using var stream = assembly.GetManifestResourceStream(resource)!;
        return JsonSerializer.Deserialize<KnownAppRule[]>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }

    private static string ExecutableName(string? path)
    {
        var clean = (path ?? "").Trim().Trim('"').Replace('\\', '/');
        return clean[(clean.LastIndexOf('/') + 1)..];
    }
    private static string ParentPath(string? path)
    {
        var clean = (path ?? "").Replace('\\', '/');
        var index = clean.LastIndexOf('/');
        return index > 0 ? clean[..index] : "";
    }
    private static bool HasPhrase(string text, string phrase)
    {
        var normalized = Normalize(phrase);
        var start = 0;
        while ((start = text.IndexOf(normalized, start, StringComparison.Ordinal)) >= 0)
        {
            var end = start + normalized.Length;
            if ((start == 0 || !char.IsLetterOrDigit(text[start - 1])) && (end == text.Length || !char.IsLetterOrDigit(text[end]))) return true;
            start++;
        }
        return false;
    }
    private static string Normalize(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            result.Append(character is 'ı' or 'İ' ? 'i' : char.ToLowerInvariant(character));
        }
        return result.ToString();
    }
}
