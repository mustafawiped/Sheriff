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
