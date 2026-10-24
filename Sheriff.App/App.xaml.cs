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
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private ShellChangeWatcher? _shellChanges;
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
        Panel.LocationRequested += app => { try { AppLauncher.OpenLocation(app); } catch (Exception ex) { Panel.UpdateStatus(ex.Message); } };
        Panel.SettingsRequested += ShowSettings;
        Panel.CategoryChangeRequested += (app, category) => _ = SaveActionAsync(() => Catalog!.AssignCategoryAsync(app, category));
        Panel.CategoryResetRequested += app => _ = SaveActionAsync(() => Catalog!.ResetCategoryAsync(app));
        Panel.PinRequested += app => _ = SaveActionAsync(() => Catalog!.TogglePinAsync(app));
        Panel.HideRequested += app => _ = SaveActionAsync(() => Catalog!.HideAsync(app));
        MainWindow = Panel;
        Panel.WarmUp();
        SystemThemeWatcher.Watch(Panel);
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Sheriff'i aç", null, (_, _) => Dispatcher.Invoke(Panel.ToggleFromInvocation));
        menu.Items.Add("Uygulamaları yenile", null, (_, _) => Catalog?.RequestRefresh());
        menu.Items.Add("Ayarlar", null, (_, _) => Dispatcher.Invoke(ShowSettings));
        menu.Items.Add("Çıkış", null, (_, _) => Dispatcher.Invoke(() => Shutdown()));
        using (var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/Sheriff.ico")).Stream)
        using (var icon = new System.Drawing.Icon(iconStream)) _trayIcon = (System.Drawing.Icon)icon.Clone();
        _tray = new System.Windows.Forms.NotifyIcon { Text = "Sheriff · Ctrl+Alt+Space", Icon = _trayIcon, ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, click) => { if (click.Button == System.Windows.Forms.MouseButtons.Left) Dispatcher.Invoke(Panel.ToggleFromInvocation); };
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
            _ = InitializeCatalogAsync();
            if (!_args.Contains("--background")) Dispatcher.BeginInvoke(Panel.ShowPanel, DispatcherPriority.ApplicationIdle);
        }
    }

    private async Task InitializeCatalogAsync()
    {
        try
        {
            var paths = await Task.Run(() => new SheriffPaths());
            var store = new JsonStore(paths);
            var config = await Task.Run(store.LoadConfig);
            Catalog = await Task.Run(() => new CatalogService(paths, store, config));
            Panel.Configure(Catalog);
            Catalog.Changed += apps => Dispatcher.BeginInvoke(() => Panel.UpdateCatalog(apps));
            Catalog.StatusChanged += status => Dispatcher.BeginInvoke(() => Panel.UpdateStatus(status));
            if (!Panel.SetHotkey(config.Hotkey)) Diagnostics.Write("HOTKEY registration failed: " + config.Hotkey);
            await Catalog.InitializeAsync(_shutdown.Token);
            _shellChanges = new ShellChangeWatcher(Panel.Handle, Catalog.RequestRefresh);
            await _shellChanges.Ready;
            Diagnostics.Write("SHELL_NOTIFICATIONS registered=" + _shellChanges.IsRegistered);
        }
        catch (Exception ex) { Diagnostics.Write("INITIALIZE " + ex); Panel.UpdateStatus("Uygulama listesi yüklenemedi. Tanılama günlüğüne bakın."); }
    }

    private async Task SaveActionAsync(Func<Task> action)
    {
        if (Catalog is null) return;
        try { await action(); }
        catch (Exception ex) { Diagnostics.Write("SAVE " + ex.Message); Panel.UpdateStatus("Değişiklik kaydedilemedi: " + ex.Message); }
    }

    private void LaunchApp(Sheriff.Core.AppEntry app, bool asAdministrator)
    {
        try { AppLauncher.Launch(app, asAdministrator); Panel.HidePanel(); if (Catalog is not null) _ = Catalog.RecordLaunchAsync(app); }
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
                _ = Dispatcher.BeginInvoke(() => { if (line == "TOGGLE") Panel.ToggleFromInvocation(); else if (line == "EXIT") Shutdown(); });
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
        _tray?.Dispose();
        _trayIcon?.Dispose();
        Panel.Dispose();
        Diagnostics.Write("EXIT");
        Diagnostics.Flush();
        base.OnExit(e);
    }
}
