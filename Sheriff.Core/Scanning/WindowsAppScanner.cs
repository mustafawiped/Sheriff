using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Sheriff.Core.Scanning;

public interface IAppIconCache
{
    string GetOrCreate(AppEntry app, string parsingName, string iconLocation, DateTimeOffset modifiedAt);
}

public sealed record ScanResult(IReadOnlyList<AppEntry> Apps, IReadOnlyList<string> Errors, bool StartMenuSucceeded, bool StoreSucceeded);

/// <summary>All Shell automation and icon extraction stays on a dedicated STA thread.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAppScanner(IAppIconCache icons, IEnumerable<string>? startMenuRoots = null, bool includeStore = true)
{
    private readonly IReadOnlyList<string> _startMenuRoots = startMenuRoots?.ToArray() ?? StartMenuDirectories;
    public static IReadOnlyList<string> StartMenuDirectories => [
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        Environment.GetFolderPath(Environment.SpecialFolder.Programs)];

    public Task<ScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        var result = new TaskCompletionSource<ScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.TrySetResult(Scan(cancellationToken)); }
            catch (OperationCanceledException) { result.TrySetCanceled(cancellationToken); }
            catch (Exception ex) { result.TrySetException(ex); }
        }) { IsBackground = true, Name = "Sheriff Shell scanner" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    private ScanResult Scan(CancellationToken token)
    {
        var apps = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        object? wscript = null;
        var startSucceeded = true;
        try
        {
            wscript = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!);
            foreach (var root in _startMenuRoots)
            {
                token.ThrowIfCancellationRequested();
                if (!Directory.Exists(root)) continue;
                foreach (var shortcut in EnumerateShortcuts(root, errors, () => startSucceeded = false))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var app = ReadShortcut(wscript!, shortcut, out var iconLocation);
                        if (app is null || apps.ContainsKey(app.Id)) continue;
                        app = app with { IconCachePath = GetIcon(app, shortcut, iconLocation, File.GetLastWriteTimeUtc(shortcut), errors) };
                        apps.Add(app.Id, app);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        errors.Add($"StartMenu item {shortcut}: {ex.Message}");
                        // Shell namespace / advertised shortcuts may not expose a WScript target.
                        // Keeping the original .lnk lets Shell resolve and launch them correctly.
                        var fallback = new AppEntry
                        {
                            Id = StableId("lnk", shortcut.ToUpperInvariant()), DisplayName = Path.GetFileNameWithoutExtension(shortcut),
                            TargetPathOrAumid = shortcut, ShortcutPath = shortcut, Source = AppSource.StartMenu,
                            InstalledAt = File.GetCreationTimeUtc(shortcut)
                        };
                        fallback = fallback with { IconCachePath = GetIcon(fallback, shortcut, "", File.GetLastWriteTimeUtc(shortcut), errors) };
                        apps.TryAdd(fallback.Id, fallback);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { startSucceeded = false; errors.Add("StartMenu enumeration: " + ex.Message); }
        finally { Release(wscript); }

        var storeSucceeded = !includeStore || ReadStoreApps(apps, errors, token);
        return new ScanResult(apps.Values.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray(), errors, startSucceeded, storeSucceeded);
    }

    private static IEnumerable<string> EnumerateShortcuts(string root, List<string> errors, Action failed)
    {
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            string[] files = [], children = [];
            try { files = Directory.GetFiles(directory, "*.lnk"); children = Directory.GetDirectories(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"StartMenu directory {directory}: {ex.Message}"); failed(); }
            foreach (var file in files) yield return file;
            foreach (var child in children)
            {
                try { if (!File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint)) directories.Push(child); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"StartMenu directory {child}: {ex.Message}"); failed(); }
            }
        }
    }

    private static AppEntry? ReadShortcut(object shell, string path, out string iconLocation)
    {
        object? shortcut = null;
        iconLocation = "";
        try
        {
            shortcut = ((dynamic)shell).CreateShortcut(path);
            dynamic link = shortcut;
            string target = Environment.ExpandEnvironmentVariables((string?)link.TargetPath ?? "");
            string args = (string?)link.Arguments ?? "";
            string description = (string?)link.Description ?? "";
            iconLocation = (string?)link.IconLocation ?? "";
            if (string.IsNullOrWhiteSpace(target)) target = path; // Advertised/MSI shortcuts still launch through Shell.
            var publisher = "";
            if (File.Exists(target))
            {
                try
                {
                    var version = FileVersionInfo.GetVersionInfo(target);
                    publisher = version.CompanyName ?? "";
                    if (string.IsNullOrWhiteSpace(description)) description = version.FileDescription ?? "";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
            }
            return new AppEntry
            {
                Id = StableId("lnk", target.Trim().ToUpperInvariant() + "\n" + args), DisplayName = Path.GetFileNameWithoutExtension(path),
                TargetPathOrAumid = target, Arguments = args, ShortcutPath = path,
                WorkingDirectory = (string?)link.WorkingDirectory ?? "", Publisher = publisher, Description = description,
                Source = AppSource.StartMenu, InstalledAt = File.GetCreationTimeUtc(path)
            };
        }
        finally { Release(shortcut); }
    }

    private bool ReadStoreApps(Dictionary<string, AppEntry> apps, List<string> errors, CancellationToken token)
    {
        object? shell = null, folder = null, items = null;
        var succeeded = true;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true)!);
            folder = ((dynamic)shell!).NameSpace("shell:AppsFolder");
            if (folder is null) throw new IOException("AppsFolder could not be opened.");
            items = ((dynamic)folder).Items();
            var count = (int)((dynamic)items).Count;
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                object? item = null;
                try
                {
                    item = ((dynamic)items).Item(i);
                    var aumid = Property(item!, "System.AppUserModel.ID");
                    if (string.IsNullOrWhiteSpace(aumid)) aumid = (string?)((dynamic)item).Path ?? "";
                    var installPath = Property(item!, "System.AppUserModel.PackageInstallPath");
                    // AppsFolder includes desktop programs too; the Start Menu pass resolves those more accurately.
                    if (string.IsNullOrWhiteSpace(aumid) || (!aumid.Contains('!') && string.IsNullOrEmpty(installPath))) continue;
                    var displayName = (string?)((dynamic)item).Name ?? aumid;
                    var app = new AppEntry
                    {
                        Id = StableId("store", aumid.ToUpperInvariant()), DisplayName = displayName,
                        TargetPathOrAumid = aumid, Publisher = Property(item!, "System.Company"),
                        Description = Property(item!, "System.FileDescription"), Source = AppSource.Store,
                        InstalledAt = Directory.Exists(installPath) ? Directory.GetCreationTimeUtc(installPath) : DateTimeOffset.UtcNow
                    };
                    var modified = Directory.Exists(installPath) ? Directory.GetLastWriteTimeUtc(installPath) : DateTime.UnixEpoch;
                    app = app with { IconCachePath = GetIcon(app, @"shell:AppsFolder\" + aumid, "", modified, errors) };
                    apps.TryAdd(app.Id, app);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add($"AppsFolder item {i}: {ex.Message}"); }
                finally { Release(item); }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { succeeded = false; errors.Add("AppsFolder enumeration: " + ex.Message); }
        finally { Release(items); Release(folder); Release(shell); }
        return succeeded;
    }

    private string GetIcon(AppEntry app, string parsingName, string iconLocation, DateTimeOffset modified, List<string> errors)
    {
        try { return icons.GetOrCreate(app, parsingName, iconLocation, modified); }
        catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add($"Icon {app.DisplayName}: {ex.Message}"); return ""; }
    }

    private static string Property(object item, string name)
    {
        try { return Convert.ToString(((dynamic)item).ExtendedProperty(name), System.Globalization.CultureInfo.InvariantCulture) ?? ""; }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return ""; }
    }

    private static string StableId(string source, string identity) => source + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.Trim()))).ToLowerInvariant();
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}
