using Microsoft.Data.Sqlite;
using Sheriff.Core.Ranking;

namespace Sheriff.Data;

/// <summary>Synchronous persistence; callers schedule it off the UI thread.</summary>
public sealed class UsageStore
{
    private readonly string _connectionString;
    private readonly object _initializeGate = new();
    private volatile bool _initialized;

    public UsageStore(SheriffPaths paths)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
    }

    public void Initialize()
    {
        if (_initialized) return;
        lock (_initializeGate)
        {
            if (_initialized) return;
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS launch_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    app_id TEXT NOT NULL,
                    launched_at INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_launch_events_app_id_launched_at
                    ON launch_events (app_id, launched_at);
                """;
            command.ExecuteNonQuery();
            _initialized = true;
        }
    }

    public void RecordLaunch(string appId, DateTimeOffset? at = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        Initialize();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO launch_events(app_id, launched_at) VALUES ($app, $at);";
        command.Parameters.AddWithValue("$app", appId);
        command.Parameters.AddWithValue("$at", (at ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    public IReadOnlyDictionary<string, UsageSummary> LoadSummaries()
    {
        Initialize();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT app_id, COUNT(*), MAX(launched_at) FROM launch_events GROUP BY app_id;";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, UsageSummary>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var appId = reader.GetString(0);
            result[appId] = new UsageSummary(appId, reader.GetInt64(1), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)));
        }
        return result;
    }

    public UsageSummary? LoadSummary(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        Initialize();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), MAX(launched_at) FROM launch_events WHERE app_id = $app;";
        command.Parameters.AddWithValue("$app", appId);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(1)) return null;
        return new UsageSummary(appId, reader.GetInt64(0), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)));
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }
}
