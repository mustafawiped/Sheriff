namespace Sheriff.Core;

/// <summary>Filters setup, removal, shell and Git utilities from the launcher by default.</summary>
public static class AutoBlockPolicy
{
    public static string? GetReason(AppEntry app)
    {
        var name = Path.GetFileNameWithoutExtension(app.DisplayName).Trim();
        var target = Path.GetFileNameWithoutExtension(app.TargetPathOrAumid).Trim();
        var exe = Path.GetFileNameWithoutExtension(app.ShortcutPath).Trim();
        var values = new[] { name, target, exe };
        static bool Has(string value, params string[] terms) => terms.Any(t => value.Contains(t, StringComparison.OrdinalIgnoreCase));
        if (values.Any(v => Has(v, "uninstall", "unins", "uninstaller", "kaldır"))) return "Kaldırma aracı";
        if (values.Any(v => Has(v, "setup", "installer", "install", "kurulum", "msiexec"))) return "Kurulum aracı";
        if (values.Any(v => Has(v, "powershell", "pwsh", "terminal", "cmd", "command prompt", "komut istemi", "git bash", "git cmd", "git gui"))) return "Terminal aracı";
        if (values.Any(v => string.Equals(v, "git", StringComparison.OrdinalIgnoreCase) || v.StartsWith("git ", StringComparison.OrdinalIgnoreCase))) return "Git aracı";
        return null;
    }

    public static bool IsBlocked(AppEntry app, SheriffConfig config) => GetReason(app) is not null
        && !(config.AllowedAutoBlockedAppIds?.Contains(app.Id) ?? false);
}
