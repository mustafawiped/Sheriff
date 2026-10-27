using Sheriff.Core;
using Sheriff.Core.Categorization;
using Xunit;

namespace Sheriff.Tests;

public sealed class CategorizerTests
{
    private static AppEntry App(string name = "Unknown", string target = @"C:\Apps\unknown.exe", string publisher = "", string description = "") => new() { Id = "test-app", DisplayName = name, TargetPathOrAumid = target, Publisher = publisher, Description = description };
    private static AppCategorizer Empty() => new([]);

    [Fact]
    public void UserOverrideWinsOverJunkAndKnownRule()
    {
        var config = new SheriffConfig();
        config.CategoryOverrides["test-app"] = Categories.Social;
        var categorizer = new AppCategorizer([new() { Name = "Known", Category = Categories.Games, ExeNames = ["unknown.exe"] }]);
        Assert.Equal(Categories.Social, categorizer.Categorize(App("Uninstall game"), config));
    }
    [Theory]
    [InlineData("Uninstall Example")]
    [InlineData("Uygulamayı KALDIR")]
    [InlineData("YARDIM")]
    [InlineData("Readme")]
    [InlineData("Release notes")]
    [InlineData("Example Updater")]
    [InlineData("VC Redistributable")]
    [InlineData("Product Documentation")]
    [InlineData("License")]
    public void JunkNamePatternsAreHiddenSystemCategory(string name) => Assert.Equal(Categories.Junk, Empty().Categorize(App(name), new()));

    [Fact]
    public void KnownExecutableUsesBasenameIgnoresCaseAndArguments()
    {
        var categorizer = new AppCategorizer([new() { Name = "VS Code", Category = Categories.Software, ExeNames = ["Code.exe"] }]);
        var app = App("Something", @"C:\Apps\CODE.EXE") with { Arguments = "--game --driver" };
        Assert.Equal(Categories.Software, categorizer.Categorize(app, new()));
    }
    [Fact]
    public void AumidIsExactCaseInsensitiveMatch()
    {
        var categorizer = new AppCategorizer([new() { Name = "Store App", Category = Categories.Social, Aumids = ["Vendor.Package_abc!App"] }]);
        Assert.Equal(Categories.Social, categorizer.Categorize(App(target: "vendor.package_ABC!app") with { Source = AppSource.Store }, new()));
        Assert.Equal(Categories.Other, categorizer.Categorize(App(target: "Vendor.Package_abc!App.Other") with { Source = AppSource.Store }, new()));
    }
    [Fact]
    public void ExactExecutableWinsOverEarlierBroadPublisherEntry()
    {
        var categorizer = new AppCategorizer([
            new() { Name = "Publisher", Category = Categories.Hardware, Publishers = ["Vendor Inc."] },
            new() { Name = "Editor", Category = Categories.Software, ExeNames = ["editor.exe"] }
        ]);
        Assert.Equal(Categories.Software, categorizer.Categorize(App(target: @"C:\Apps\editor.exe", publisher: "Vendor Inc."), new()));
    }
    [Fact]
    public void KnownMatchesKeepRuleOrderAndSkipDisabledCategories()
    {
        var categorizer = new AppCategorizer([
            new() { Name = "First", Category = Categories.Hardware, ExeNames = [" EDITOR.EXE "], Publishers = [" Vendor Inc. "] },
            new() { Name = "Second", Category = Categories.Software, ExeNames = ["editor.exe"], Publishers = ["Vendor Inc."] }
        ]);
        var config = new SheriffConfig();
        var app = App(target: @"C:\Apps\editor.exe", publisher: "vendor inc.");
        Assert.Equal(Categories.Hardware, categorizer.Categorize(app, config));
        config.Categories.Remove(Categories.Hardware);
        Assert.Equal(Categories.Software, categorizer.Categorize(app, config));
        Assert.Equal(Categories.Software, categorizer.Categorize(app with { TargetPathOrAumid = @"C:\Apps\other.exe" }, config));
    }
    [Fact]
    public void KnownPublisherMatchesExactly()
    {
        var categorizer = new AppCategorizer([new() { Name = "Vendor", Category = Categories.Social, Publishers = ["Example Publisher"] }]);
        Assert.Equal(Categories.Social, categorizer.Categorize(App(publisher: "EXAMPLE PUBLISHER"), new()));
        Assert.Equal(Categories.Other, categorizer.Categorize(App(publisher: "Fake Example Publisher Evil"), new()));
    }
    [Theory]
    [InlineData("NVIDIA Corporation")]
    [InlineData("Logitech, Inc.")]
    [InlineData("HyperX")]
    [InlineData("Advanced Micro Devices, Inc.")]
    [InlineData("Intel Corporation")]
    public void HardwarePublisherRulesMatch(string publisher) => Assert.Equal(Categories.Hardware, Empty().Categorize(App(publisher: publisher), new()));
    [Fact]
    public void GameFolderRuleMatchesSteamapps() => Assert.Equal(Categories.Games, Empty().Categorize(App(target: @"D:\Steam\steamapps\common\Example\example.exe"), new()));
    [Fact]
    public void FolderRuleDoesNotMatchInsideAnotherFolderName() => Assert.Equal(Categories.Other, Empty().Categorize(App(target: @"D:\mysteamappscopy\example.exe"), new()));
    [Theory]
    [InlineData("Unknown terminal", "", Categories.Software)]
    [InlineData("Unknown", "Firmware driver manager", Categories.Hardware)]
    [InlineData("Example", "database client", Categories.Server)]
    [InlineData("Unknown", "chat client", Categories.Social)]
    [InlineData("Example game", "", Categories.Games)]
    public void KeywordScoreUsesNameAndDescription(string name, string description, string category) => Assert.Equal(category, Empty().Categorize(App(name, description: description), new()));
    [Theory]
    [InlineData("Code")]
    [InlineData("Launcher")]
    [InlineData("Terminally cheerful")]
    [InlineData("Driverless")]
    [InlineData("Digital")]
    [InlineData("Shelpful")]
    public void WeakOrSubstringKeywordDoesNotClassify(string name) => Assert.Equal(Categories.Other, Empty().Categorize(App(name), new()));
    [Fact]
    public void UnknownAppIsMarkedForOptionalLlmFallback()
    {
        var result = Empty().Classify(App("Unfamiliar"), new());
        Assert.Equal(Categories.Other, result.Category);
        Assert.True(result.IsUnclassified);
    }
    [Fact]
    public void ExplicitOtherOverrideNeverBecomesLlmCandidate()
    {
        var config = new SheriffConfig();
        config.CategoryOverrides["test-app"] = Categories.Other;
        Assert.False(Empty().Classify(App(), config).IsUnclassified);
    }
    [Fact]
    public void RemovedAutomaticCategoryFallsBackToOther()
    {
        var config = new SheriffConfig();
        config.Categories.Remove(Categories.Software);
        var categorizer = new AppCategorizer([new() { Name = "Code", Category = Categories.Software, ExeNames = ["Code.exe"] }]);
        Assert.Equal(Categories.Other, categorizer.Categorize(App("SDK", @"C:\Code.exe"), config));
    }
    [Fact]
    public void CustomUserCategoryIsPreserved()
    {
        var config = new SheriffConfig();
        config.Categories.Add("İş");
        config.CategoryOverrides["test-app"] = "İş";
        Assert.Equal("İş", Empty().Categorize(App("Uninstall"), config));
    }
    [Fact]
    public void MissingUserCategoryDoesNotAllowAutomaticRulesToOverwriteChoice()
    {
        var config = new SheriffConfig();
        config.CategoryOverrides["test-app"] = "Removed category";
        var result = Empty().Classify(App("Game"), config);
        Assert.Equal(Categories.Other, result.Category);
        Assert.False(result.IsUnclassified);
        Assert.Equal("Removed category", config.CategoryOverrides["test-app"]);
    }
}
