namespace Sheriff.Data;

public sealed class SheriffPaths
{
    public string Root { get; }
    public string Icons => Path.Combine(Root, "icons");
    public string ConfigFile => Path.Combine(Root, "config.json");
    public string AppsFile => Path.Combine(Root, "apps.json");
    public string DatabaseFile => Path.Combine(Root, "usage.db");
    public string Logs => Path.Combine(Root, "logs");
    public SheriffPaths(string? root = null)
    {
        Root = root ?? Environment.GetEnvironmentVariable("SHERIFF_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sheriff");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Icons);
        Directory.CreateDirectory(Logs);
    }
}
