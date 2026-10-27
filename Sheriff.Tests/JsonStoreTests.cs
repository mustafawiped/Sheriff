using Sheriff.Core;
using Sheriff.Data;
using Xunit;

namespace Sheriff.Tests;

public sealed class JsonStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Sheriff-tests-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void ConfigRoundTripPreservesUserOverridesAndPins()
    {
        var store = new JsonStore(new SheriffPaths(_root));
        var config = new SheriffConfig();
        config.CategoryOverrides["APP"] = Categories.Hardware;
        config.PinnedAppIds.Add("APP");
        store.SaveConfig(config);
        var restored = store.LoadConfig();
        Assert.Equal(Categories.Hardware, restored.CategoryOverrides["app"]);
        Assert.Contains("app", restored.PinnedAppIds);
    }
    [Fact]
    public void DisabledCustomCategorySurvivesReloadAndCanRestoreManualAssignment()
    {
        var store = new JsonStore(new SheriffPaths(_root));
        var config = new SheriffConfig { AvailableCategories = ["Özel"] };
        config.CategoryOverrides["app"] = "Özel";
        store.SaveConfig(config);
        var restored = store.LoadConfig();
        Assert.Contains("Özel", restored.AvailableCategories);
        Assert.DoesNotContain("Özel", restored.Categories);
        var categorizer = new Sheriff.Core.Categorization.AppCategorizer([]);
        var app = new AppEntry { Id = "app" };
        Assert.Equal(Categories.Other, categorizer.Categorize(app, restored));
        restored.Categories.Add("Özel");
        Assert.Equal("Özel", categorizer.Categorize(app, restored));
    }
    [Fact]
    public void LegacyConfigRecoversCategoriesFromAssignments()
    {
        var paths = new SheriffPaths(_root);
        File.WriteAllText(paths.ConfigFile, "{\"Categories\":[\"Yazılım\"],\"CategoryOverrides\":{\"app\":\"Özel\"}}");
        var config = new JsonStore(paths).LoadConfig();
        Assert.Contains("Özel", config.AvailableCategories);
        Assert.Contains(Categories.Games, config.AvailableCategories);
        Assert.DoesNotContain(Categories.Games, config.Categories);
    }
    [Fact]
    public void CategoryLabelsColorsAndAssignmentsSurviveReload()
    {
        var store = new JsonStore(new SheriffPaths(_root));
        var config = new SheriffConfig();
        var id = CategoryCustomization.AddCategory(config, "Ofis");
        CategoryCustomization.RenameCategory(config, id, "Çalışma");
        CategoryCustomization.SetColor(config, id, "#12abef");
        CategoryCustomization.AssignCategory(config, "word", id);
        store.SaveConfig(config);
        var loaded = store.LoadConfig();
        Assert.Equal("Çalışma", loaded.CategoryLabels["OFIS"]);
        Assert.Equal("#12ABEF", loaded.CategoryColors["OFIS"]);
        Assert.Equal(id, loaded.CategoryOverrides["WORD"]);
        Assert.Contains(id, loaded.Categories);
    }
    [Fact]
    public void HandEditedPresentationMetadataIsNormalizedWithoutLosingAssignments()
    {
        var paths = new SheriffPaths(_root);
        File.WriteAllText(paths.ConfigFile, """
            {"CategoryLabels":{" Yazılım ":" Kod Araçları ","Donanım":""},
             "CategoryColors":{"Yazılım":" #a1b2c3 ","Oyun":"invalid"},
             "CategoryOverrides":{"APP":"Yazılım"}}
            """);
        var loaded = new JsonStore(paths).LoadConfig();
        Assert.Equal("Kod Araçları", loaded.CategoryLabels[Categories.Software]);
        Assert.False(loaded.CategoryLabels.ContainsKey(Categories.Hardware));
        Assert.Equal("#A1B2C3", loaded.CategoryColors[Categories.Software]);
        Assert.False(loaded.CategoryColors.ContainsKey(Categories.Games));
        Assert.Equal(Categories.Software, loaded.CategoryOverrides["app"]);
    }
    [Fact]
    public void CacheRoundTripPreservesLaunchArgumentsAndSource()
    {
        var store = new JsonStore(new SheriffPaths(_root));
        var app = new AppEntry { Id = "a", DisplayName = "Example", TargetPathOrAumid = "C:\\Example\\app.exe", Arguments = "--profile \"personal profile\"", Source = AppSource.StartMenu };
        store.SaveApps([app]);
        Assert.Equal(app, Assert.Single(store.LoadApps()));
    }
    [Fact]
    public void MalformedCacheFallsBackWithoutThrowing()
    {
        var paths = new SheriffPaths(_root);
        File.WriteAllText(paths.AppsFile, "{unfinished");
        Assert.Empty(new JsonStore(paths).LoadApps());
        Assert.True(File.Exists(Path.Combine(paths.Logs, "storage.log")));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
