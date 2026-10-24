namespace Sheriff.Core.Ranking;

public sealed record UsageSummary(string AppId, long LaunchCount, DateTimeOffset? LastLaunchedAt);

/// <summary>Ranking uses only already-loaded state. The clock is supplied by the caller.</summary>
public sealed class AppRanker
{
    public const double PinBonus = 100;
    public const double HalfLifeDays = 30;

    public double Score(AppEntry app, UsageSummary? usage, bool pinned, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(app);
        var launches = Math.Max(0, usage?.LaunchCount ?? 0);
        var usageScore = 0d;
        if (launches > 0 && usage?.LastLaunchedAt is { } last)
        {
            var age = Math.Max(0, (now - last).TotalDays);
            usageScore = launches * Math.Exp(-Math.Log(2) * age / HalfLifeDays);
        }
        return usageScore + (pinned ? PinBonus : 0);
    }

    public IReadOnlyList<AppEntry> RankApps(IEnumerable<AppEntry> apps, IReadOnlyDictionary<string, UsageSummary> usage, IReadOnlySet<string> pinned, DateTimeOffset now)
    {
        return apps.OrderByDescending(app => Score(app, FindUsage(usage, app.Id), pinned.Contains(app.Id), now))
            .ThenBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(app => app.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<string> RankCategories(IEnumerable<AppEntry> apps, IReadOnlyDictionary<string, UsageSummary> usage, IReadOnlySet<string> pinned, IEnumerable<string> configuredCategories, DateTimeOffset now)
    {
        var configured = configuredCategories.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var order = configured.Select((category, index) => (category, index)).ToDictionary(pair => pair.category, pair => pair.index, StringComparer.OrdinalIgnoreCase);
        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            var category = string.IsNullOrWhiteSpace(app.Category) ? Categories.Other : app.Category;
            totals.TryGetValue(category, out var total);
            totals[category] = total + Score(app, FindUsage(usage, app.Id), pinned.Contains(app.Id), now);
        }
        return configured.Concat(totals.Keys).Append(Categories.Other).Append(Categories.Junk)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(TailPosition)
            .ThenByDescending(category => totals.GetValueOrDefault(category))
            .ThenBy(category => order.GetValueOrDefault(category, int.MaxValue))
            .ThenBy(category => category, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static UsageSummary? FindUsage(IReadOnlyDictionary<string, UsageSummary> usage, string id) => usage.TryGetValue(id, out var summary) ? summary : null;
    private static int TailPosition(string category) => string.Equals(category, Categories.Junk, StringComparison.OrdinalIgnoreCase) ? 2 : string.Equals(category, Categories.Other, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
}
