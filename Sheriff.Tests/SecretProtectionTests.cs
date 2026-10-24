using Sheriff.Core;
using Sheriff.Data;
using Xunit;

namespace Sheriff.Tests;

public sealed class SecretProtectionTests
{
    [Fact]
    public void ConfigStoresProtectedKeyAndRestoresItForCurrentWindowsUser()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "Sheriff-secret-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new SheriffPaths(root);
            var store = new JsonStore(paths);
            const string key = "local-fake-key-only-for-test";
            store.SaveConfig(new SheriffConfig { LlmApiKey = key });
            var text = File.ReadAllText(paths.ConfigFile);
            Assert.DoesNotContain(key, text);
            Assert.Contains("dpapi:", text);
            Assert.Equal(key, store.LoadConfig().LlmApiKey);
            store.SaveConfig(new SheriffConfig { LlmApiKey = "" });
            Assert.Empty(store.LoadConfig().LlmApiKey);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
