using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sheriff.Core;
using Sheriff.Data;

namespace Sheriff.App;

public partial class App
{
    private async Task UiSmokeAsync()
    {
        try
        {
            var paths = new SheriffPaths();
            Catalog = new CatalogService(paths, new JsonStore(paths), new SheriffConfig(), [], includeStore: false);
            Panel.Configure(Catalog);
            var sampleApps = new[]
            {
                SmokeApp("ui-software-1", "Not Defteri", Categories.Software),
                SmokeApp("ui-software-2", "Kod Düzenleyici", Categories.Software),
                SmokeApp("ui-software-3", "Metin Aracı", Categories.Software),
                SmokeApp("ui-software-4", "Sözlük", Categories.Software),
                SmokeApp("ui-hardware", "Aygıt Yöneticisi", Categories.Hardware),
                SmokeApp("ui-social", "Sohbet", Categories.Social),
                SmokeApp("ui-game", "Oyun Merkezi", Categories.Games),
                SmokeApp("ui-server", "Sunucu Paneli", Categories.Server),
                SmokeApp("ui-other", "Örnek Uygulama", Categories.Other),
                SmokeApp("ui-junk", "Yardımcı Kurulum", Categories.Junk)
            };
            Panel.UpdateCatalog(sampleApps);
            RenderPanel("ui-overview.png");
            var overview = Panel.CategoryOverviewVisible && Panel.CategoryCardCount == Categories.Defaults.Length && Panel.VisibleAppCount == sampleApps.Length;

            Panel.OpenCategory(Categories.Software);
            var detail = !Panel.CategoryOverviewVisible && Panel.VisibleAppCount == 4;
            Panel.UpdateLayout();
            RenderPanel("ui-category-detail.png");
            var hoverStarted = Panel.BeginHoverDelayForSmoke("ui-software-1");
            await Task.Delay(550);
            var hoverActions = hoverStarted && Panel.HoverActionsVisibleForSmoke("ui-software-1");

            Panel.ShowAllCategories();
            AppEntry? requestedLaunch = null;
            Panel.LaunchRequested -= LaunchApp;
            Action<AppEntry, bool> launchProbe = (app, _) => requestedLaunch = app;
            Panel.LaunchRequested += launchProbe;
            var previewClicked = Panel.RaisePreviewClickForSmoke("ui-software-1");
            Panel.LaunchRequested -= launchProbe;
            var previewLaunch = previewClicked && requestedLaunch?.Id == "ui-software-1";
            var removeClicked = Panel.RaiseRemoveActionForSmoke("ui-software-1");
            await Task.Delay(150);
            var removedPersisted = removeClicked && new JsonStore(paths).LoadConfig().HiddenAppIds.Contains("ui-software-1");

            var result = new { overview, detail, hoverActions, previewLaunch, removedPersisted, categories = Categories.Defaults.Length, appCount = sampleApps.Length };
            File.WriteAllText(Path.Combine(paths.Root, "ui-smoke.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Diagnostics.Write("UI_SMOKE " + JsonSerializer.Serialize(result));
            Shutdown(overview && detail && hoverActions && previewLaunch && removedPersisted ? 0 : 1);
        }
        catch (Exception ex) { Diagnostics.Write("UI_SMOKE FAIL " + ex); Shutdown(1); }
    }

    private void RenderPanel(string name)
    {
        if (Panel.Content is not FrameworkElement content) throw new InvalidOperationException("Panel content is unavailable.");
        content.Measure(new Size(688, 680));
        content.Arrange(new Rect(0, 0, 688, 680));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(688, 680, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(Diagnostics.Root, name));
        encoder.Save(output);
    }

    private static AppEntry SmokeApp(string id, string name, string category) => new()
    {
        Id = id,
        DisplayName = name,
        TargetPathOrAumid = id,
        Category = category,
        Source = AppSource.StartMenu
    };

    private async Task PerformanceSmokeAsync()
    {
        try
        {
            await InitializeCatalogAsync();
            Panel.SuppressAutoHide = true;
            var handle = Panel.Handle;
            var samples = new List<double>();
            for (var i = 0; i < 25; i++)
            {
                var rendered = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnRendered(double elapsed) => rendered.TrySetResult(elapsed);
                Panel.ShowRendered += OnRendered;
                try
                {
                    Panel.ShowPanel();
                    samples.Add(await rendered.Task.WaitAsync(TimeSpan.FromSeconds(3)));
                    await Task.Delay(120);
                    if (Panel.Handle != handle) throw new InvalidOperationException("Window was recreated.");
                }
                finally { Panel.ShowRendered -= OnRendered; Panel.HidePanel(); }
                await Task.Delay(50);
            }
            var sorted = samples.Order().ToArray();
            var p95 = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1];
            var result = new { apps = Catalog?.Apps.Count, samplesMs = samples, medianMs = sorted[sorted.Length / 2], p95Ms = p95,
                maxMs = sorted[^1], targetP95Under50Ms = p95 < 50, sameHwnd = true, Panel.BackdropEnabled, Panel.AnimationEnabled,
                metric = "Show to first WPF Rendering callback; not physical monitor presentation" };
            File.WriteAllText(Path.Combine(Diagnostics.Root, "performance-smoke.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(p95 < 50 ? 0 : 1);
        }
        catch (Exception ex) { Diagnostics.Write("PERF_SMOKE " + ex); Shutdown(1); }
    }

    private async Task WatchSmokeAsync()
    {
        try
        {
            var paths = new SheriffPaths();
            var store = new JsonStore(paths);
            var fixture = Path.Combine(paths.Root, "fixture-start-menu");
            Directory.CreateDirectory(fixture);
            using var catalog = new CatalogService(paths, store, store.LoadConfig(), [fixture], includeStore: false);
            await catalog.InitializeAsync(_shutdown.Token);
            var shortcutPath = Path.Combine(fixture, "Sheriff USB Driver.lnk");
            object? shell = null, shortcut = null;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!);
                shortcut = ((dynamic)shell!).CreateShortcut(shortcutPath);
                ((dynamic)shortcut).TargetPath = Path.Combine(fixture, "fixture-driver.exe");
                ((dynamic)shortcut).IconLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll") + ",0";
                ((dynamic)shortcut).Save();
            }
            finally
            {
                if (shortcut is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
                if (shell is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
            await WaitUntilAsync(() => catalog.Apps.Any(a => a.ShortcutPath == shortcutPath));
            var detectedMs = timer.ElapsedMilliseconds;
            var app = catalog.Apps.Single(a => a.ShortcutPath == shortcutPath);
            var autoCategorized = app.Category == Categories.Hardware;
            var newBadge = app.IsNew;
            await catalog.AssignCategoryAsync(app, Categories.Software);
            await catalog.TogglePinAsync(app);
            await catalog.HideAsync(app);
            await catalog.RefreshAsync(_shutdown.Token);
            var overridden = catalog.Apps.Single().Category == Categories.Software;
            var saved = store.LoadConfig();
            var persisted = saved.CategoryOverrides.GetValueOrDefault(app.Id) == Categories.Software && saved.PinnedAppIds.Contains(app.Id) && saved.HiddenAppIds.Contains(app.Id);
            var hiddenFromFrequent = catalog.FrequentApps.Count == 0;
            await catalog.ResetCategoryAsync(app);
            var reset = catalog.Apps.Single().Category == Categories.Hardware;
            File.Delete(shortcutPath);
            await WaitUntilAsync(() => catalog.Apps.Count == 0);
            var deleted = catalog.Apps.Count == 0;
            var result = new { detectedMs, autoCategorized, newBadge, overridden, persisted, hiddenFromFrequent, reset, deleted };
            File.WriteAllText(Path.Combine(paths.Root, "watcher-smoke.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(autoCategorized && newBadge && overridden && persisted && hiddenFromFrequent && reset && deleted ? 0 : 1);
        }
        catch (Exception ex) { Diagnostics.Write("WATCH_SMOKE " + ex); Shutdown(1); }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > timeout) throw new TimeoutException("Watcher condition timed out.");
            await Task.Delay(100);
        }
    }

    private async Task RankSmokeAsync()
    {
        try
        {
            await InitializeCatalogAsync();
            var catalog = Catalog ?? throw new InvalidOperationException("Catalog unavailable");
            var used = catalog.Apps.First(a => a.Category == Categories.Software);
            var pinned = catalog.Apps.First(a => a.Category == Categories.Hardware);
            for (var i = 0; i < 3; i++) await catalog.RecordLaunchAsync(used);
            catalog.Config.PinnedAppIds.Add(pinned.Id);
            await catalog.ApplySettingsAsync(catalog.Config);
            var usage = new UsageStore(new SheriffPaths()).LoadSummaries();
            var persisted = usage[used.Id].LaunchCount == 3;
            var pinFirst = catalog.FrequentApps.First().Id == pinned.Id;
            var categoryFirst = catalog.CategoryOrder.First() == Categories.Hardware;
            var tailFixed = catalog.CategoryOrder.TakeLast(2).SequenceEqual(new[] { Categories.Other, Categories.Junk });
            var result = new { persisted, pinFirst, categoryFirst, tailFixed, frequent = catalog.FrequentApps.Select(a => a.DisplayName), categoryOrder = catalog.CategoryOrder };
            File.WriteAllText(Path.Combine(Diagnostics.Root, "ranking-smoke.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(persisted && pinFirst && categoryFirst && tailFixed ? 0 : 1);
        }
        catch (Exception ex) { Diagnostics.Write("RANK_SMOKE " + ex); Shutdown(1); }
    }
}
