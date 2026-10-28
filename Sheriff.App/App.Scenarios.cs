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
            var store = new JsonStore(paths);
            var config = new SheriffConfig();
            Catalog = new CatalogService(paths, store, config, [], includeStore: false);
            var sampleApps = new[]
            {
                SmokeApp("ui-software-1", "Not Defteri", Categories.Software),
                SmokeApp("ui-software-2", "Kod Düzenleyici", Categories.Software),
                SmokeApp("ui-software-3", "Metin Aracı", Categories.Software),
                SmokeApp("ui-software-4", "Sözlük", Categories.Software),
                SmokeApp("ui-software-5", "Takvim", Categories.Software),
                SmokeApp("ui-software-6", "Hesap Makinesi", Categories.Software),
                SmokeApp("ui-software-7", "Terminal", Categories.Software),
                SmokeApp("ui-software-8", "Dosyalar", Categories.Software),
                SmokeApp("ui-hardware", "Aygıt Yöneticisi", Categories.Hardware),
                SmokeApp("ui-social", "Sohbet", Categories.Social),
                SmokeApp("ui-game", "Oyun Merkezi", Categories.Games),
                SmokeApp("ui-server", "Sunucu Paneli", Categories.Server),
                SmokeApp("ui-other", "Örnek Uygulama", Categories.Other),
                SmokeApp("ui-junk", "Yardımcı Kurulum", Categories.Junk)
            };
            var iconSources = new[] { "notepad.exe", "control.exe", "cmd.exe", "mstsc.exe", "Taskmgr.exe", "WindowsPowerShell\\v1.0\\powershell.exe" };
            sampleApps = sampleApps.Select((app, index) => app with
            {
                IconCachePath = Catalog.Icons.GetOrCreate(app, Path.Combine(Environment.SystemDirectory, iconSources[index % iconSources.Length]), "", DateTimeOffset.UnixEpoch)
            }).ToArray();
            foreach (var app in sampleApps) config.CategoryOverrides[app.Id] = app.Category;
            store.SaveApps(sampleApps);
            var usage = new UsageStore(paths);
            for (var rank = 1; rank <= 8; rank++)
                for (var launch = 0; launch < 9 - rank; launch++) usage.RecordLaunch("ui-software-" + rank);
            await Catalog.InitializeAsync(_shutdown.Token, refreshImmediately: false);
            Panel.Configure(Catalog);
            Panel.SuppressAutoHide = true;
            Panel.ShowPanel();
            await Task.Delay(200);
            var firstCard = SmokeVisuals<FrameworkElement>((FrameworkElement)Panel.Content)
                .First(element => element.Name == "CardRoot" && element.DataContext is CatalogCategoryCard { Name: Categories.Software });
            var root = (FrameworkElement)Panel.Content;
            var beforeHover = firstCard.TransformToAncestor(root).TransformBounds(new Rect(firstCard.RenderSize));
            firstCard.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
            await Task.Delay(70);
            var duringHover = firstCard.TransformToAncestor(root).TransformBounds(new Rect(firstCard.RenderSize));
            var hoverBoundsStable = Math.Abs(beforeHover.X - duringHover.X) < 0.1 && Math.Abs(beforeHover.Y - duringHover.Y) < 0.1
                && Math.Abs(beforeHover.Width - duringHover.Width) < 0.1 && Math.Abs(beforeHover.Height - duringHover.Height) < 0.1;
            await Task.Delay(250);
            firstCard.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = System.Windows.Input.Mouse.MouseLeaveEvent });
            await Task.Delay(160);
            var previewOrder = Catalog.CategoryPreviewApps.Where(a => a.Category == Categories.Software).Take(7).Select(a => a.Id)
                .SequenceEqual(Enumerable.Range(1, 8).Where(i => i != 7).Select(i => "ui-software-" + i));
            RenderPanel("ui-overview.png");
            var layout = SmokePreviewLayout();
            var expectedVisible = sampleApps.Count(app => !AutoBlockPolicy.IsBlocked(app, config));
            var overview = Panel.CategoryOverviewVisible && Panel.CategoryCardCount == Categories.Defaults.Length && Panel.VisibleAppCount == expectedVisible;

            Panel.CategoryRequested -= ShowCategoryDetail;
            Panel.OpenCategory(Categories.Software);
            var detailWindow = new CategoryDetailWindow(Categories.Software, config, Catalog.Apps, Catalog.Icons) { Owner = Panel };
            detailWindow.Loaded += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                detailWindow.UpdateLayout();
                RenderWindow(detailWindow, "ui-category-detail.png");
                detailWindow.Close();
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            detailWindow.ShowDialog();
            var detail = ((System.Windows.Controls.ListBox)detailWindow.FindName("AppRows")).Items.Count == 2;
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
            var editorChecks = SmokeCategoryEditor(sampleApps, paths);

            var result = new { overview, detail, hoverActions, hoverBoundsStable, previewLaunch, removedPersisted, previewOrder, layout, editorChecks, categories = Categories.Defaults.Length, appCount = sampleApps.Length };
            File.WriteAllText(Path.Combine(paths.Root, "ui-smoke.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Diagnostics.Write("UI_SMOKE " + JsonSerializer.Serialize(result));
            Shutdown(overview && detail && hoverActions && hoverBoundsStable && previewLaunch && removedPersisted && previewOrder && layout.MatchesReference && editorChecks.Values.All(passed => passed) ? 0 : 1);
        }
        catch (Exception ex) { Diagnostics.Write("UI_SMOKE FAIL " + ex); Shutdown(1); }
    }

    private Dictionary<string, bool> SmokeCategoryEditor(IReadOnlyList<AppEntry> apps, SheriffPaths paths)
    {
        var checks = new Dictionary<string, bool>();
        var original = new SheriffConfig { LlmApiKey = "local-smoke-fixture" };
        foreach (var app in apps) original.CategoryOverrides[app.Id] = app.Category;
        Exception? failure = null;
        var cancelled = new CategoryManagerWindow(original, apps) { Owner = Panel };
        cancelled.Loaded += (_, _) => cancelled.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                ((System.Windows.Controls.TextBox)cancelled.FindName("NewCategoryBox")).Text = "İptal Denemesi";
                SmokeClick(cancelled, "AddCategoryButton");
                checks["cancelDraftWasEdited"] = cancelled.Result.Categories.Contains("İptal Denemesi");
                cancelled.Width = 820 * InterfaceMetrics.Scale;
                cancelled.Height = 540 * InterfaceMetrics.Scale;
                cancelled.UpdateLayout();
                RenderWindow(cancelled, "ui-category-editor-compact.png");
                var content = (FrameworkElement)cancelled.Content;
                var appsList = (FrameworkElement)cancelled.FindName("AppsGrid");
                var transfer = (FrameworkElement)cancelled.FindName("TransferPanel");
                var accept = (FrameworkElement)cancelled.FindName("AcceptButton");
                var listBottom = appsList.TranslatePoint(new Point(0, appsList.ActualHeight), content).Y;
                var transferTop = transfer.TranslatePoint(new Point(), content).Y;
                var transferBottom = transfer.TranslatePoint(new Point(0, transfer.ActualHeight), content).Y;
                var acceptTop = accept.TranslatePoint(new Point(), content).Y;
                checks["compactLayout"] = appsList.ActualHeight >= 110 && listBottom <= transferTop + 1 && transferBottom <= acceptTop + 1;
            }
            catch (Exception ex) { failure = ex; }
            finally { cancelled.Close(); }
        });
        cancelled.ShowDialog();
        if (failure is not null) throw failure;
        checks["cancelPreservesOriginal"] = !original.Categories.Contains("İptal Denemesi");

        var editor = new CategoryManagerWindow(original, apps) { Owner = Panel };
        editor.Loaded += (_, _) => editor.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                ((System.Windows.Controls.TextBox)editor.FindName("NewCategoryBox")).Text = "Odak";
                SmokeClick(editor, "AddCategoryButton");
                ((System.Windows.Controls.TextBox)editor.FindName("CategoryNameBox")).Text = "Çalışma";
                SmokeClick(editor, "RenameCategoryButton");
                ((System.Windows.Controls.TextBox)editor.FindName("ColorBox")).Text = "#5b9fe6";
                SmokeClick(editor, "ApplyColorButton");
                checks["renameAndColor"] = CategoryCustomization.GetDisplayName(editor.Result, "Odak") == "Çalışma"
                    && CategoryCustomization.GetColor(editor.Result, "Odak") == "#5B9FE6";
                var scope = (System.Windows.Controls.ComboBox)editor.FindName("ScopeBox");
                var search = (System.Windows.Controls.TextBox)editor.FindName("AppSearchBox");
                var grid = (System.Windows.Controls.DataGrid)editor.FindName("AppsGrid");
                scope.SelectedIndex = 1;
                search.Text = "Not Defteri";
                checks["search"] = grid.Items.Count == 1;
                ((System.Windows.Controls.ComboBox)editor.FindName("TargetCategoryBox")).SelectedValue = "Odak";
                grid.UpdateLayout();
                var singleRow = grid.ItemContainerGenerator.ContainerFromIndex(0) as System.Windows.Controls.DataGridRow;
                var checkbox = singleRow is null ? null : SmokeVisuals<System.Windows.Controls.CheckBox>(singleRow).FirstOrDefault();
                checks["membershipInitiallyUnchecked"] = checkbox?.IsChecked == false;
                if (checkbox is not null)
                {
                    checkbox.IsChecked = true;
                    checks["membershipAddsImmediately"] = editor.Result.CategoryOverrides.Values.Contains("Odak");
                    checkbox.IsChecked = false;
                    checks["membershipRemovesImmediately"] = editor.Result.CategoryOverrides.Values.Contains(Categories.Other);
                }
                search.Clear();
                grid.SelectedItems.Add(grid.Items[0]);
                grid.SelectedItems.Add(grid.Items[1]);
                ((System.Windows.Controls.ComboBox)editor.FindName("TargetCategoryBox")).SelectedValue = "Odak";
                SmokeClick(editor, "MoveAppsButton");
                checks["batchAssignment"] = editor.Result.CategoryOverrides.Values.Count(id => id == "Odak") == 2;
                checks["draftIsolation"] = !original.Categories.Contains("Odak") && !original.CategoryOverrides.Values.Contains("Odak")
                    && editor.Result.LlmApiKey == original.LlmApiKey;
                scope.SelectedIndex = 0;
                checks["categoryFilter"] = grid.Items.Count == 2;
                RenderWindow(editor, "ui-category-editor.png");
                grid.SelectedIndex = 0;
                SmokeClick(editor, "ResetAppsButton");
                checks["automaticReset"] = editor.Result.CategoryOverrides.Values.Count(id => id == "Odak") == 1;
                SmokeClick(editor, "AcceptButton");
            }
            catch (Exception ex) { failure = ex; editor.Close(); }
        });
        checks["accepted"] = editor.ShowDialog() == true;
        if (failure is not null) throw failure;
        var store = new JsonStore(new SheriffPaths(Path.Combine(paths.Root, "editor-result")));
        store.SaveConfig(editor.Result);
        var reloaded = store.LoadConfig();
        checks["persistence"] = CategoryCustomization.GetDisplayName(reloaded, "Odak") == "Çalışma"
            && CategoryCustomization.GetColor(reloaded, "Odak") == "#5B9FE6" && reloaded.CategoryOverrides.Values.Count(id => id == "Odak") == 1;
        return checks;
    }

    private static void SmokeClick(FrameworkElement window, string name)
    {
        var button = (System.Windows.Controls.Primitives.ButtonBase)window.FindName(name);
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, button));
    }

    private sealed record PreviewIconPosition(string Id, double X, double Y);
    private sealed record PreviewLayoutResult(bool MatchesReference, double CardWidth, double CardHeight, IReadOnlyList<PreviewIconPosition> Icons, IReadOnlyList<PreviewIconPosition> MiniIcons);

    private PreviewLayoutResult SmokePreviewLayout()
    {
        var root = (FrameworkElement)Panel.Content;
        var surface = SmokeVisuals<System.Windows.Controls.Border>(root).First(e => e.Name == "CardSurface" && e.DataContext is CatalogCategoryCard { Name: Categories.Software });
        var origin = surface.TranslatePoint(new Point(), root);
        var icons = SmokeVisuals<System.Windows.Controls.Image>(surface).Where(e => e.ActualWidth >= 39 && e.DataContext is AppTile)
            .Select(e => new PreviewIconPosition(((AppTile)e.DataContext).Entry.Id, e.TranslatePoint(new Point(e.ActualWidth / 2, e.ActualHeight / 2), root).X - origin.X,
                e.TranslatePoint(new Point(e.ActualWidth / 2, e.ActualHeight / 2), root).Y - origin.Y)).ToArray();
        var miniIcons = SmokeVisuals<System.Windows.Controls.Image>(surface).Where(e => e.ActualWidth <= 21 && e.DataContext is AppTile)
            .Select(e => new PreviewIconPosition(((AppTile)e.DataContext).Entry.Id, e.TranslatePoint(new Point(e.ActualWidth / 2, e.ActualHeight / 2), root).X - origin.X,
                e.TranslatePoint(new Point(e.ActualWidth / 2, e.ActualHeight / 2), root).Y - origin.Y)).ToArray();
        var expected = new[] { new Point(55, 55), new Point(139, 55), new Point(55, 139) };
        var expectedMini = new[] { new Point(128, 128), new Point(150, 128), new Point(128, 150), new Point(150, 150) };
        var matches = Math.Abs(surface.ActualWidth - 194) < 1 && Math.Abs(surface.ActualHeight - 194) < 1
            && icons.Length == 3 && miniIcons.Length == 4
            && icons.Select((icon, index) => Math.Abs(icon.X - expected[index].X) < 1.5 && Math.Abs(icon.Y - expected[index].Y) < 1.5).All(pass => pass)
            && miniIcons.Select((icon, index) => Math.Abs(icon.X - expectedMini[index].X) < 1.5 && Math.Abs(icon.Y - expectedMini[index].Y) < 1.5).All(pass => pass);
        return new(matches, surface.ActualWidth, surface.ActualHeight, icons, miniIcons);
    }

    private static IEnumerable<T> SmokeVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in SmokeVisuals<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private void RenderPanel(string name)
    {
        RenderWindow(Panel, name);
    }

    private static void RenderWindow(Window window, string name)
    {
        if (window.Content is not FrameworkElement content) throw new InvalidOperationException("Window content is unavailable.");
        var width = (int)window.Width;
        var height = (int)window.Height;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        // Mica is composed by Windows and is absent from an off-screen bitmap.
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 32, 32)), null, new Rect(0, 0, width, height));
        bitmap.Render(background);
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
