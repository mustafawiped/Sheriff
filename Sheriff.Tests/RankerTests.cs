using Sheriff.Core;
using Sheriff.Core.Ranking;
using Xunit;

namespace Sheriff.Tests;

public sealed class RankerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private readonly AppRanker _ranker = new();
    private static AppEntry App(string id, string name = "Example", string category = Categories.Software) => new() { Id = id, DisplayName = name, Category = category };
    private static Dictionary<string, UsageSummary> Usage(params UsageSummary[] summaries) => summaries.ToDictionary(s => s.AppId, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ThirtyDaysHalvesUsageScore()
    {
        var recent = _ranker.Score(App("a"), new("a", 20, Now), false, Now);
        var aged = _ranker.Score(App("a"), new("a", 20, Now.AddDays(-30)), false, Now);
        Assert.Equal(20, recent, 8);
        Assert.Equal(recent / 2, aged, 8);
    }
    [Fact]
    public void RecentUsageCanOutrankMoreOldLaunches()
    {
        var apps = new[] { App("old"), App("recent") };
        var usage = Usage(new("old", 60, Now.AddDays(-120)), new("recent", 8, Now.AddDays(-1)));
        Assert.Equal("recent", _ranker.RankApps(apps, usage, new HashSet<string>(), Now)[0].Id);
    }
    [Fact]
    public void PinBonusAppliesWithoutHistory()
    {
        var pinned = App("pinned", "Zeta");
        var recent = App("recent", "Alpha");
        Assert.Equal(AppRanker.PinBonus, _ranker.Score(pinned, null, true, Now));
        Assert.Equal("pinned", _ranker.RankApps([recent, pinned], Usage(new UsageSummary("recent", 40, Now)), new HashSet<string> { "pinned" }, Now)[0].Id);
    }
    [Fact]
    public void FutureTimestampCannotAmplifyLaunchCount() => Assert.Equal(12, _ranker.Score(App("a"), new("a", 12, Now.AddDays(10)), false, Now), 8);
    [Fact]
    public void MissingOrInvalidHistoryHasZeroScore()
    {
        Assert.Equal(0, _ranker.Score(App("a"), null, false, Now));
        Assert.Equal(0, _ranker.Score(App("a"), new("a", -10, Now), false, Now));
        Assert.Equal(0, _ranker.Score(App("a"), new("a", 5, null), false, Now));
    }
    [Fact]
    public void EqualScoresUseDisplayNameThenId()
    {
        var result = _ranker.RankApps([App("z", "Beta"), App("b", "Alpha"), App("a", "Alpha")], Usage(), new HashSet<string>(), Now);
        Assert.Equal(new[] { "a", "b", "z" }, result.Select(a => a.Id));
    }
    [Fact]
    public void CategoriesUseSumOfAppScores()
    {
        var apps = new[] { App("s1"), App("s2"), App("g", category: Categories.Games) };
        var usage = Usage(new("s1", 4, Now), new("s2", 4, Now), new("g", 7, Now));
        var result = _ranker.RankCategories(apps, usage, new HashSet<string>(), [Categories.Games, Categories.Software], Now);
        Assert.Equal(Categories.Software, result[0]);
    }
    [Fact]
    public void OtherAndJunkAlwaysRemainAtTailDespiteHugeUsage()
    {
        var apps = new[] { App("s"), App("o", category: Categories.Other), App("j", category: Categories.Junk) };
        var usage = Usage(new("o", 10000, Now), new("j", 100000, Now));
        var result = _ranker.RankCategories(apps, usage, new HashSet<string> { "o", "j" }, Categories.Defaults, Now);
        Assert.Equal(Categories.Software, result[0]);
        Assert.Equal(Categories.Other, result[^2]);
        Assert.Equal(Categories.Junk, result[^1]);
    }
    [Fact]
    public void EqualCategoryScoresRespectConfiguredOrderAndIncludeEmptyCategories()
    {
        var result = _ranker.RankCategories([], Usage(), new HashSet<string>(), ["İş", Categories.Server, Categories.Games], Now);
        Assert.Equal(new[] { "İş", Categories.Server, Categories.Games, Categories.Other, Categories.Junk }, result);
    }
    [Fact]
    public void SameTimestampWithDifferentOffsetsProducesSameScore()
    {
        Assert.Equal(_ranker.Score(App("a"), new("a", 5, Now.AddDays(-30)), false, Now), _ranker.Score(App("a"), new("a", 5, Now.AddDays(-30).ToOffset(TimeSpan.FromHours(3))), false, Now), 8);
    }
}
