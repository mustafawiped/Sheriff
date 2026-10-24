using Microsoft.Data.Sqlite;
using Sheriff.Data;
using Xunit;

namespace Sheriff.Tests;

public sealed class UsageStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Sheriff-usage-tests-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void EventsSurviveReopenAndSummaryUsesNewestTimestamp()
    {
        var paths = new SheriffPaths(_root);
        var latest = new DateTimeOffset(2026, 10, 24, 15, 42, 0, TimeSpan.FromHours(3));
        var store = new UsageStore(paths);
        store.RecordLaunch("example", latest);
        store.RecordLaunch("example", latest.AddDays(-3));
        store.RecordLaunch("other", latest.AddMinutes(-1));
        var reopened = new UsageStore(paths).LoadSummaries();
        Assert.Equal(2, reopened["example"].LaunchCount);
        Assert.Equal(latest.ToUniversalTime(), reopened["example"].LastLaunchedAt);
        Assert.Equal(1, reopened["other"].LaunchCount);
    }
    [Fact]
    public void UnicodeAndSqlTextAreStoredAsAnOpaqueAppId()
    {
        var store = new UsageStore(new SheriffPaths(_root));
        const string appId = "İş uygulaması'; DROP TABLE launch_events; --";
        store.RecordLaunch(appId);
        store.RecordLaunch("normal");
        var result = store.LoadSummaries();
        Assert.Equal(1, result[appId].LaunchCount);
        Assert.Equal(1, result["normal"].LaunchCount);
    }
    [Fact]
    public void InitializeIsIdempotentAndCreatesWalDatabase()
    {
        var paths = new SheriffPaths(_root);
        var store = new UsageStore(paths);
        store.Initialize();
        store.Initialize();
        Assert.Empty(store.LoadSummaries());
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabaseFile, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", Convert.ToString(command.ExecuteScalar()));
    }
    [Fact]
    public void ConcurrentLaunchEventsAreNotLost()
    {
        var store = new UsageStore(new SheriffPaths(_root));
        Parallel.For(0, 24, _ => store.RecordLaunch("app"));
        Assert.Equal(24, store.LoadSummaries()["app"].LaunchCount);
    }
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptyIdsAreRejected(string id) => Assert.Throws<ArgumentException>(() => new UsageStore(new SheriffPaths(_root)).RecordLaunch(id));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
