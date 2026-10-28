using System.Text.Json;
using Sheriff.Core;

namespace Sheriff.Data;

public sealed class JsonStore(SheriffPaths paths)
{
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All) };
    public SheriffConfig LoadConfig()
    {
        var config = Read<SheriffConfig>(paths.ConfigFile) ?? new();
        config.Categories ??= [.. Categories.Defaults];
        config.Categories = config.Categories.Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim()).Where(c => !string.Equals(c, Categories.Other, StringComparison.OrdinalIgnoreCase) && !string.Equals(c, Categories.Junk, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var required in new[] { Categories.Other, Categories.Junk })
            config.Categories.Add(required);
        CategoryCustomization.NormalizeMetadata(config);
        config.AvailableCategories = (config.AvailableCategories ?? []).Concat(config.Categories)
            .Concat(config.CategoryOverrides?.Values ?? Enumerable.Empty<string>())
            .Concat(config.CategoryLabels.Keys).Concat(config.CategoryColors.Keys)
            .Concat(Categories.Defaults).Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        config.CategoryOverrides = new(config.CategoryOverrides ?? [], StringComparer.OrdinalIgnoreCase);
        config.PinnedAppIds = new(config.PinnedAppIds ?? [], StringComparer.OrdinalIgnoreCase);
        config.HiddenAppIds = new(config.HiddenAppIds ?? [], StringComparer.OrdinalIgnoreCase);
        config.AllowedAutoBlockedAppIds = new(config.AllowedAutoBlockedAppIds ?? [], StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows() && !string.IsNullOrEmpty(config.LlmProtectedApiKey))
        {
            try { config.LlmApiKey = SecretProtection.Unprotect(config.LlmProtectedApiKey); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FormatException) { Log("API anahtarı bu Windows kullanıcısıyla çözülemedi."); }
        }
        return config;
    }
    public void SaveConfig(SheriffConfig config)
    {
        if (OperatingSystem.IsWindows()) config.LlmProtectedApiKey = SecretProtection.Protect(config.LlmApiKey);
        Write(paths.ConfigFile, config);
    }
    public List<AppEntry> LoadApps() => Read<List<AppEntry>>(paths.AppsFile) ?? [];
    public void SaveApps(IEnumerable<AppEntry> apps) => Write(paths.AppsFile, apps.ToList());
    private T? Read<T>(string path)
    {
        lock (_gate)
        {
            try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : default; }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Log($"{Path.GetFileName(path)}: {ex.Message}");
                return default;
            }
        }
    }
    private void Log(string message)
    {
        try { File.AppendAllText(Path.Combine(paths.Logs, "storage.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private void Write<T>(string path, T value)
    {
        lock (_gate)
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options));
            File.Move(temporary, path, true);
        }
    }
}
