using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Sheriff.Data;
using Sheriff.Core.Scanning;

namespace Sheriff.App;

public partial class App : System.Windows.Application
{
    private readonly string[] _args;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _pendingActionsGate = new();
    private readonly HashSet<Task> _pendingActions = [];
    private ShellChangeWatcher? _shellChanges;
    private bool _interactiveMode, _categoryRefreshRequested, _dismissalShutdownStarted;
    internal MainWindow Panel { get; private set; } = null!;
    internal CatalogService? Catalog { get; private set; }

    public App(string[] args)
    {
        _args = args;
        InitializeComponent();
        DispatcherUnhandledException += (_, e) => { Diagnostics.Write("ERROR " + e.Exception); e.Handled = true; };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplicationThemeManager.ApplySystemTheme();
        Panel = new MainWindow();
        if (Environment.GetEnvironmentVariable("SHERIFF_UI_TEST") == "1") { Panel.ShowInTaskbar = true; Panel.SuppressAutoHide = true; }
        Panel.LaunchRequested += LaunchApp;
        Panel.CategoryOpened += OnCategoryOpened;
        Panel.CategoryRequested += ShowCategoryDetail;
        Panel.Dismissed += OnPanelDismissed;
        Panel.LocationRequested += app => { try { AppLauncher.OpenLocation(app); } catch (Exception ex) { Panel.UpdateStatus(ex.Message); } };
        Panel.SettingsRequested += ShowSettings;
        Panel.CategoryChangeRequested += (app, category) => QueueSaveAction(() => Catalog!.AssignCategoryAsync(app, category));
        Panel.CategoryResetRequested += app => QueueSaveAction(() => Catalog!.ResetCategoryAsync(app));
        Panel.PinRequested += app => QueueSaveAction(() => Catalog!.TogglePinAsync(app));
        Panel.HideRequested += app => QueueSaveAction(() => Catalog!.HideAsync(app));
        MainWindow = Panel;
        Panel.WarmUp();
        SystemThemeWatcher.Watch(Panel);
        _ = ListenAsync();
        Diagnostics.Write("READY hwnd=" + Panel.Handle);
        if (_args.Contains("--smoke-test"))
            Dispatcher.BeginInvoke(async () => await SmokeTestAsync(), DispatcherPriority.ApplicationIdle);
        else if (_args.Contains("--scan-smoke"))
            Dispatcher.BeginInvoke(async () => await ScanSmokeAsync(), DispatcherPriority.ApplicationIdle);
        else if (_args.Contains("--rank-smoke"))
            Dispatcher.BeginInvoke(async () => await RankSmokeAsync(), DispatcherPriority.ApplicationIdle);
        else if (_args.Contains("--watch-smoke"))
            Dispatcher.BeginInvoke(async () => await WatchSmokeAsync(), DispatcherPriority.ApplicationIdle);
        else if (_args.Contains("--perf-smoke"))
            Dispatcher.BeginInvoke(async () => await PerformanceSmokeAsync(), DispatcherPriority.ApplicationIdle);
        else if (_args.Contains("--catalog-smoke"))
            Dispatcher.BeginInvoke(async () =>
            {
                await InitializeCatalogAsync();
                var summary = Catalog?.Apps.GroupBy(a => a.Category).ToDictionary(g => g.Key, g => g.Count());
                File.WriteAllText(Path.Combine(Diagnostics.Root, "catalog-smoke.json"), System.Text.Json.JsonSerializer.Serialize(summary, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                Shutdown(Catalog?.Apps.Count > 0 ? 0 : 1);
            }, DispatcherPriority.ApplicationIdle);
        else if (_args.Contains("--ui-smoke"))
            Dispatcher.BeginInvoke(async () => await UiSmokeAsync(), DispatcherPriority.ApplicationIdle);
        else
        {
            _interactiveMode = true;
            _ = InitializeCatalogAsync(showPanel: true, refreshImmediately: false);
        }
    }

    private async Task InitializeCatalogAsync(bool showPanel = false, bool refreshImmediately = true)
    {
        try
        {
            var paths = await Task.Run(() => new SheriffPaths());
            var store = new JsonStore(paths);
            var config = await Task.Run(store.LoadConfig);
            Catalog = await Task.Run(() => new CatalogService(paths, store, config));
            Panel.Configure(Catalog);
            var cacheReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Catalog.Changed += apps =>
            {
                void ApplyCatalog()
                {
                    Panel.UpdateCatalog(apps);
                    cacheReady.TrySetResult(true);
                }
                if (Dispatcher.CheckAccess()) ApplyCatalog();
                else _ = Dispatcher.BeginInvoke(ApplyCatalog);
            };
            Catalog.StatusChanged += status =>
            {
                if (Dispatcher.CheckAccess()) Panel.UpdateStatus(status);
                else _ = Dispatcher.BeginInvoke(() => Panel.UpdateStatus(status));
            };
            if (!Panel.SetHotkey(config.Hotkey)) Diagnostics.Write("HOTKEY registration failed: " + config.Hotkey);
            var initialization = Catalog.InitializeAsync(_shutdown.Token, refreshImmediately);
            if (showPanel)
            {
                // Show as soon as the disk cache is applied. Usage indexing and fresh scans can finish later.
                await Task.WhenAny(cacheReady.Task, initialization);
                if (!cacheReady.Task.IsCompletedSuccessfully) await initialization;
                _categoryRefreshRequested = Catalog.Apps.Count == 0;
                Panel.ShowPanel();
                if (_categoryRefreshRequested) Catalog.RequestRefresh();
            }
            await initialization;
            _shellChanges = new ShellChangeWatcher(Panel.Handle, Catalog.RequestRefresh);
            await _shellChanges.Ready;
            Diagnostics.Write("SHELL_NOTIFICATIONS registered=" + _shellChanges.IsRegistered);
        }
        catch (Exception ex)
        {
            Diagnostics.Write("INITIALIZE " + ex);
            Panel.UpdateStatus("Uygulama listesi yüklenemedi. Tanılama günlüğüne bakın.");
            if (showPanel && !Dispatcher.HasShutdownStarted && !Panel.IsVisible) Panel.ShowPanel();
        }
    }

    private async Task SaveActionAsync(Func<Task> action)
    {
        if (Catalog is null) return;
        try { await action(); }
        catch (Exception ex) { Diagnostics.Write("SAVE " + ex.Message); Panel.UpdateStatus("Değişiklik kaydedilemedi: " + ex.Message); }
    }

    private void QueueSaveAction(Func<Task> action) => TrackPendingAction(SaveActionAsync(action));

    private void TrackPendingAction(Task task)
    {
        lock (_pendingActionsGate) _pendingActions.Add(task);
        _ = task.ContinueWith(completed =>
        {
            lock (_pendingActionsGate) _pendingActions.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void OnCategoryOpened()
    {
        if (!_interactiveMode || _categoryRefreshRequested || Catalog is null) return;
        _categoryRefreshRequested = true;
        Catalog.RequestRefresh();
    }

    private void ShowCategoryDetail(string categoryId)
    {
        if (Catalog is null) return;
        if (!Panel.IsVisible) Panel.ShowPanel();
        var wasSuppressed = Panel.SuppressAutoHide;
        Panel.SuppressAutoHide = true;
        CategoryDetailWindow? window = null;
        Action<IReadOnlyList<Sheriff.Core.AppEntry>>? catalogChanged = null;
        try
        {
            window = new CategoryDetailWindow(categoryId, Catalog.Config, Catalog.Apps, Catalog.Icons) { Owner = Panel };
            catalogChanged = apps =>
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (window.IsVisible && Catalog is not null) window.UpdateApps(apps, Catalog.Config);
                });
            };
            Catalog.Changed += catalogChanged;
            window.PinRequested += app => QueueSaveAction(() => Catalog.TogglePinAsync(app));
            window.HideRequested += app => QueueSaveAction(() => Catalog.HideAsync(app));
            window.CategoryChangeRequested += (app, target) => QueueSaveAction(() => Catalog.AssignCategoryAsync(app, target));
            window.CategoryResetRequested += app => QueueSaveAction(() => Catalog.ResetCategoryAsync(app));
            window.LocationRequested += app => { try { AppLauncher.OpenLocation(app); } catch (Exception ex) { Panel.UpdateStatus(ex.Message); } };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            Diagnostics.Write("CATEGORY_DETAIL " + ex);
            Panel.UpdateStatus("Kategori açılamadı: " + ex.Message);
        }
        finally
        {
            if (catalogChanged is not null) Catalog.Changed -= catalogChanged;
            Panel.ShowAllCategories();
            Panel.SuppressAutoHide = wasSuppressed;
        }
        if (window?.LaunchEntry is { } app) LaunchApp(app, window.LaunchAsAdministrator);
    }

    private async void OnPanelDismissed()
    {
        if (!_interactiveMode || _dismissalShutdownStarted) return;
        _dismissalShutdownStarted = true;
        NativeWindow.UnregisterShortcut(Panel.Handle);
        Task[] pending;
        lock (_pendingActionsGate) pending = [.. _pendingActions];
        if (pending.Length > 0)
        {
            try { await Task.WhenAll(pending); }
            catch (Exception ex) { Diagnostics.Write("PENDING ACTION " + ex.Message); }
        }
        if (!Dispatcher.HasShutdownStarted) Shutdown();
    }

    private void LaunchApp(Sheriff.Core.AppEntry app, bool asAdministrator)
    {
        try
        {
            var wasSuppressed = Panel.SuppressAutoHide;
            Panel.SuppressAutoHide = true;
            try { AppLauncher.Launch(app, asAdministrator); }
            finally { Panel.SuppressAutoHide = wasSuppressed; }
            if (Catalog is not null) TrackPendingAction(Catalog.RecordLaunchAsync(app));
            Panel.HidePanel();
        }
        catch (Exception ex) { Diagnostics.Write("LAUNCH " + ex.Message); Panel.UpdateStatus("Başlatılamadı: " + ex.Message); }
    }

    private async void ShowSettings()
    {
        if (Catalog is null) { Panel.UpdateStatus("Ayarlar yükleniyor…"); return; }
        if (!Panel.IsVisible) Panel.ShowPanel();
        Panel.SuppressAutoHide = true;
        try
        {
            var settings = new SettingsWindow(Catalog.Config, Catalog.Apps) { Owner = Panel };
            if (settings.ShowDialog() != true) return;
            if (!Panel.SetHotkey(settings.Result.Hotkey)) { Panel.UpdateStatus("Kısayol kaydedilemedi; önceki kısayol korunuyor."); return; }
            if (settings.Result.StartWithWindows != Catalog.Config.StartWithWindows) NativeWindow.SetStartup(settings.Result.StartWithWindows);
            await Catalog.ApplySettingsAsync(settings.Result);
            Panel.UpdateStatus("Ayarlar kaydedildi.");
        }
        catch (Exception ex) { Diagnostics.Write("SETTINGS " + ex); Panel.UpdateStatus("Ayarlar kaydedilemedi: " + ex.Message); }
        finally { Panel.SuppressAutoHide = Environment.GetEnvironmentVariable("SHERIFF_UI_TEST") == "1"; }
    }

    private async Task ScanSmokeAsync()
    {
        try
        {
            var paths = new SheriffPaths();
            var scanner = new WindowsAppScanner(new ShellIconCache(paths.Icons));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var first = await scanner.ScanAsync(_shutdown.Token);
            var firstMs = clock.ElapsedMilliseconds;
            var files = Directory.GetFiles(paths.Icons, "*.png");
            var stamps = files.ToDictionary(f => f, File.GetLastWriteTimeUtc);
            clock.Restart();
            var second = await scanner.ScanAsync(_shutdown.Token);
            var cacheStable = stamps.All(s => File.Exists(s.Key) && File.GetLastWriteTimeUtc(s.Key) == s.Value);
            var result = new { firstMs, secondMs = clock.ElapsedMilliseconds, apps = first.Apps.Count, startMenu = first.Apps.Count(a => a.Source == Sheriff.Core.AppSource.StartMenu), store = first.Apps.Count(a => a.Source == Sheriff.Core.AppSource.Store), icons = files.Length, cacheStable, first.StartMenuSucceeded, first.StoreSucceeded, errors = first.Errors };
            new JsonStore(paths).SaveApps(first.Apps);
            File.WriteAllText(Path.Combine(paths.Root, "scanner-smoke.json"), System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Diagnostics.Write("SCANNER_SMOKE " + System.Text.Json.JsonSerializer.Serialize(result));
            Shutdown(first.Apps.Count > 0 && files.Length > 0 && cacheStable ? 0 : 1);
        }
        catch (Exception ex) { Diagnostics.Write("SCANNER_SMOKE FAIL " + ex); Shutdown(1); }
    }

    private async Task ListenAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(Program.PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(_shutdown.Token).ConfigureAwait(false);
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (line == "TOGGLE" && !_dismissalShutdownStarted) Panel.ToggleFromInvocation();
                    else if (line == "EXIT") Shutdown();
                });
            }
            catch (OperationCanceledException) { break; }
            catch (IOException ex) { Diagnostics.Write("PIPE " + ex.Message); }
        }
    }

    private async Task SmokeTestAsync()
    {
        try
        {
            var handle = Panel.Handle;
            Panel.ShowPanel();
            await Task.Delay(100);
            var shown = Panel.IsVisible;
            Panel.HidePanel();
            var hidden = !Panel.IsVisible;
            Panel.ShowPanel();
            await Task.Delay(100);
            var reused = Panel.Handle == handle && Panel.IsVisible;
            Panel.HidePanel();
            var sent = await Task.Run(() => Program.SendCommand("TOGGLE"));
            await Task.Delay(100);
            var pipeToggled = Panel.IsVisible;
            Panel.HidePanel();
            Diagnostics.Write($"SMOKE shown={shown} hidden={hidden} sameHwnd={reused} hotkey={Panel.HotkeyRegistered} pipe={sent && pipeToggled}");
            File.WriteAllText(Path.Combine(Diagnostics.Root, "smoke.json"), System.Text.Json.JsonSerializer.Serialize(new { shown, hidden, sameHwnd = reused, hotkey = Panel.HotkeyRegistered, pipe = sent && pipeToggled }));
            Shutdown(shown && hidden && reused && Panel.HotkeyRegistered && sent && pipeToggled ? 0 : 1);
        }
        catch (Exception ex) { Diagnostics.Write("SMOKE FAIL " + ex); Shutdown(1); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdown.Cancel();
        Catalog?.Dispose();
        _shellChanges?.Dispose();
        Panel.Dispose();
        Diagnostics.Write("EXIT");
        Diagnostics.Flush();
        base.OnExit(e);
    }
}
