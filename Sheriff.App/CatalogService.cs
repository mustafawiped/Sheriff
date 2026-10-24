using System.IO;
using Sheriff.Core;
using Sheriff.Core.Scanning;
using Sheriff.Core.Categorization;
using Sheriff.Data;
using Sheriff.Core.Ranking;

namespace Sheriff.App;

public sealed class CatalogService : IDisposable
{
    private readonly SheriffPaths _paths;
    private readonly JsonStore _store;
    private readonly WindowsAppScanner _scanner;
    private readonly AppCategorizer _categorizer = new();
    private readonly LlmCategorizer _llm;
    private readonly Dictionary<string, string> _llmAssignments = new(StringComparer.Ordinal);
    private int _llmRunning, _configVersion;
    private CancellationToken _lifetime;
    private readonly AppRanker _ranker = new();
    private readonly UsageStore _usage;
    private IReadOnlyDictionary<string, UsageSummary> _summaries = new Dictionary<string, UsageSummary>();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly string[] _scanRoots;
    private StartMenuWatcher? _watcher;
    private AppEntry[] _apps = [];
    private bool _baseline;
    private readonly object _configGate = new();
    public SheriffConfig Config { get; private set; }
    public ShellIconCache Icons { get; }
    public IReadOnlyList<AppEntry> Apps => Volatile.Read(ref _apps);
    public IReadOnlyList<AppEntry> FrequentApps { get; private set; } = [];
    public IReadOnlyList<string> CategoryOrder { get; private set; } = Categories.Defaults;
    public event Action<IReadOnlyList<AppEntry>>? Changed;
    public event Action<string>? StatusChanged;

    public CatalogService(SheriffPaths paths, JsonStore store, SheriffConfig config, IEnumerable<string>? scanRoots = null, bool includeStore = true)
    {
        _paths = paths; _store = store; Config = config;
        _usage = new UsageStore(paths);
        _llm = new LlmCategorizer(Path.Combine(paths.Root, "llm-cache.json"));
        _scanRoots = (scanRoots ?? WindowsAppScanner.StartMenuDirectories).ToArray();
        Icons = new ShellIconCache(paths.Icons);
        _scanner = new WindowsAppScanner(Icons, _scanRoots, includeStore);
    }

    public async Task InitializeAsync(CancellationToken token)
    {
        _lifetime = token;
        if (!File.Exists(_paths.ConfigFile)) await Task.Run(() => _store.SaveConfig(Config), token);
        try { await Task.Run(() => { _usage.Initialize(); _summaries = _usage.LoadSummaries(); }, token); }
        catch (Exception ex) { Diagnostics.Write("USAGE INIT " + ex.Message); }
        var cached = await Task.Run(_store.LoadApps, token);
        await Task.Run(() => Icons.WarmImages(cached), token);
        _baseline = cached.Count > 0;
        Publish(cached);
        _watcher = new StartMenuWatcher(_scanRoots, RefreshAsync, token);
        _watcher.Error += ex => Diagnostics.Write("WATCHER " + ex.Message);
        await RefreshAsync(token);
    }

    public async Task RefreshAsync(CancellationToken token = default)
    {
        await _scanGate.WaitAsync(token);
        try
        {
            StatusChanged?.Invoke("Uygulamalar arka planda taranıyor…");
            var result = await _scanner.ScanAsync(token);
            var previous = Apps.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
            var next = result.Apps.Select(a => previous.TryGetValue(a.Id, out var old)
                ? a with { InstalledAt = old.InstalledAt, IsNew = old.IsNew }
                : a with { IsNew = _baseline }).ToList();
            if (!result.StartMenuSucceeded) next.AddRange(previous.Values.Where(a => a.Source == AppSource.StartMenu && !next.Any(n => n.Id == a.Id)));
            if (!result.StoreSucceeded) next.AddRange(previous.Values.Where(a => a.Source == AppSource.Store && !next.Any(n => n.Id == a.Id)));
            _baseline = true;
            Publish(next);
            ScheduleLlm();
            await Task.Run(() => _store.SaveApps(Apps), token);
            foreach (var error in result.Errors) Diagnostics.Write("SCAN " + error);
            StatusChanged?.Invoke($"{Apps.Count} uygulama · Hazır");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Diagnostics.Write("SCAN ERROR " + ex); StatusChanged?.Invoke("Tarama tamamlanamadı. Önbellek kullanılmaya devam ediyor."); }
        finally { _scanGate.Release(); }
    }

    private void Publish(IEnumerable<AppEntry> apps)
    {
        lock (_configGate)
        {
            var classified = apps.GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .Select(a =>
                {
                    var result = _categorizer.Classify(a, Config);
                    var category = result.Category;
                    if (result.IsUnclassified && Config.LlmEnabled && _llmAssignments.TryGetValue(LlmKey(a), out var inferred) && Config.Categories.Contains(inferred)) category = inferred;
                    return a with { Category = category };
                }).ToArray();
            var now = DateTimeOffset.UtcNow;
            var ranked = _ranker.RankApps(classified, _summaries, Config.PinnedAppIds, now);
            var visible = ranked.Where(a => !Config.HiddenAppIds.Contains(a.Id)).ToArray();
            CategoryOrder = _ranker.RankCategories(visible, _summaries, Config.PinnedAppIds, Config.Categories, now);
            FrequentApps = visible.Where(a => a.Category != Categories.Junk && (_summaries.ContainsKey(a.Id) || Config.PinnedAppIds.Contains(a.Id))).Take(8).ToArray();
            Volatile.Write(ref _apps, ranked.ToArray());
        }
        Changed?.Invoke(Apps);
    }

    public async Task RecordLaunchAsync(AppEntry app)
    {
        try
        {
            await Task.Run(() =>
            {
                _usage.RecordLaunch(app.Id);
                var summary = _usage.LoadSummaries();
                lock (_configGate) _summaries = summary;
            });
            lock (_configGate) Publish(Apps.Select(a => a.Id == app.Id ? a with { IsNew = false } : a));
            await Task.Run(() => _store.SaveApps(Apps));
        }
        catch (Exception ex) { Diagnostics.Write("USAGE " + ex.Message); }
    }

    public async Task ApplySettingsAsync(SheriffConfig config)
    {
        lock (_configGate) { Config = config; _configVersion++; _llmAssignments.Clear(); Publish(Apps); }
        await Task.Run(() => { lock (_configGate) _store.SaveConfig(Config); });
        ScheduleLlm();
    }

    public Task AssignCategoryAsync(AppEntry app, string category) => MutateAsync(config =>
    {
        if (!config.Categories.Contains(category, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Bilinmeyen kategori.");
        config.CategoryOverrides[app.Id] = category;
    });
    public Task ResetCategoryAsync(AppEntry app) => MutateAsync(config => config.CategoryOverrides.Remove(app.Id));
    public Task TogglePinAsync(AppEntry app) => MutateAsync(config => { if (!config.PinnedAppIds.Add(app.Id)) config.PinnedAppIds.Remove(app.Id); });
    public Task HideAsync(AppEntry app) => MutateAsync(config => config.HiddenAppIds.Add(app.Id));
    private async Task MutateAsync(Action<SheriffConfig> mutate)
    {
        lock (_configGate) { mutate(Config); Publish(Apps); }
        await Task.Run(() => { lock (_configGate) _store.SaveConfig(Config); });
        ScheduleLlm();
    }
    public void RequestRefresh() => _watcher?.RequestRefresh();

    private static string LlmKey(AppEntry app) => app.Id + "\n" + app.DisplayName + "\n" + app.Publisher;
    private void ScheduleLlm()
    {
        SheriffConfig snapshot;
        AppEntry[] candidates;
        int version;
        lock (_configGate)
        {
            if (!Config.LlmEnabled || string.IsNullOrWhiteSpace(Config.LlmApiKey) || _lifetime.IsCancellationRequested) return;
            if (Interlocked.Exchange(ref _llmRunning, 1) != 0) return;
            version = _configVersion;
            snapshot = new SheriffConfig { Categories = [.. Config.Categories], CategoryOverrides = new(Config.CategoryOverrides, StringComparer.OrdinalIgnoreCase),
                LlmEnabled = true, LlmApiKey = Config.LlmApiKey, LlmEndpoint = Config.LlmEndpoint, LlmModel = Config.LlmModel };
            candidates = Apps.Where(a => !Config.HiddenAppIds.Contains(a.Id) && !_llmAssignments.ContainsKey(LlmKey(a)) && _categorizer.Classify(a, Config).IsUnclassified).ToArray();
        }
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var app in candidates)
                {
                    lock (_configGate)
                    {
                        if (!Config.LlmEnabled || version != _configVersion || _lifetime.IsCancellationRequested) break;
                        if (Config.HiddenAppIds.Contains(app.Id) || !_categorizer.Classify(app, Config).IsUnclassified) continue;
                    }
                    var category = await _llm.ClassifyAsync(app, snapshot, _lifetime);
                    if (category is null) continue;
                    lock (_configGate)
                    {
                        if (!Config.LlmEnabled || version != _configVersion || Config.HiddenAppIds.Contains(app.Id) || !_categorizer.Classify(app, Config).IsUnclassified) continue;
                        _llmAssignments[LlmKey(app)] = category;
                        Publish(Apps);
                    }
                }
                await Task.Run(() => _store.SaveApps(Apps));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Diagnostics.Write("LLM background failed: " + ex.GetType().Name); }
            finally
            {
                Interlocked.Exchange(ref _llmRunning, 0);
                if (version != Volatile.Read(ref _configVersion)) ScheduleLlm();
            }
        });
    }

    public void Dispose() { _watcher?.Dispose(); _llm.Dispose(); }
}
