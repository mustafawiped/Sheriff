using System.Diagnostics;
using System.IO;
using Sheriff.Core;

namespace Sheriff.App;

public static class AppLauncher
{
    public static void Launch(AppEntry app, bool asAdministrator = false)
    {
        if (app.Source == AppSource.Store)
        {
            if (asAdministrator) throw new InvalidOperationException("Store uygulamaları yönetici olarak başlatılamaz.");
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            info.ArgumentList.Add(@"shell:AppsFolder\" + app.TargetPathOrAumid);
            Process.Start(info);
            return;
        }
        // Shell launch preserves advertised/MSI shortcut behavior and its AppUserModelID.
        var useShortcut = !asAdministrator && !string.IsNullOrEmpty(app.ShortcutPath) && File.Exists(app.ShortcutPath);
        var start = new ProcessStartInfo(useShortcut ? app.ShortcutPath : app.TargetPathOrAumid)
        {
            UseShellExecute = true,
            Arguments = useShortcut ? "" : app.Arguments,
            Verb = asAdministrator ? "runas" : ""
        };
        if (!string.IsNullOrEmpty(app.WorkingDirectory) && Directory.Exists(app.WorkingDirectory)) start.WorkingDirectory = app.WorkingDirectory;
        Process.Start(start);
    }

    public static void OpenLocation(AppEntry app)
    {
        var path = app.Source == AppSource.Store ? @"shell:AppsFolder" : !string.IsNullOrEmpty(app.ShortcutPath) ? app.ShortcutPath : app.TargetPathOrAumid;
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        if (app.Source == AppSource.Store) info.ArgumentList.Add(path);
        else info.Arguments = "/select,\"" + path.Replace("\"", "") + "\"";
        Process.Start(info);
    }
}
