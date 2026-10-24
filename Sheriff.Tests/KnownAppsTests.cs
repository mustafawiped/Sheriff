using Sheriff.Core;
using Sheriff.Core.Categorization;
using Xunit;

namespace Sheriff.Tests;

public sealed class KnownAppsTests
{
    [Fact]
    public void EmbeddedDictionaryContainsAtLeastTwoHundredDistinctApps()
    {
        var rules = AppCategorizer.LoadKnownApps();
        Assert.True(rules.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 200);
        Assert.All(rules, r => Assert.Contains(r.Category, Categories.Defaults));
    }
    [Fact]
    public void ExecutableAliasesDoNotConflictAcrossCategories()
    {
        var aliases = AppCategorizer.LoadKnownApps().SelectMany(rule => rule.ExeNames.Select(exe => (exe, rule.Category)));
        Assert.All(aliases.GroupBy(a => a.exe, StringComparer.OrdinalIgnoreCase), group => Assert.Single(group.Select(a => a.Category).Distinct()));
    }
    [Theory]
    [InlineData("code.exe", Categories.Software)]
    [InlineData("Cursor.exe", Categories.Software)]
    [InlineData("lghub.exe", Categories.Hardware)]
    [InlineData("NVIDIA app.exe", Categories.Hardware)]
    [InlineData("Discord.exe", Categories.Social)]
    [InlineData("steam.exe", Categories.Games)]
    [InlineData("EpicGamesLauncher.exe", Categories.Games)]
    public void CommonApplicationsHaveAnExactMapping(string executable, string expected)
    {
        Assert.Equal(expected, new AppCategorizer().Categorize(new AppEntry { Id = executable, TargetPathOrAumid = executable }, new SheriffConfig()));
    }
}
