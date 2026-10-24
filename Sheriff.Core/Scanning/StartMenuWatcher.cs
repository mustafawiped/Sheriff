using System.Diagnostics;
using System.Threading.Channels;

namespace Sheriff.Core.Scanning;

/// <summary>Coalesces Shell shortcut changes and runs at most one background refresh at a time.</summary>
public sealed class StartMenuWatcher : IDisposable
{
    private readonly string[] _roots;
    private readonly Func<CancellationToken, Task> _refresh;
    private readonly TimeSpan _debounce;
    private readonly CancellationTokenSource _stop;
    private readonly object _watchGate = new();
    private readonly Dictionary<string, Registration> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite
    });
    private readonly Task _worker;
    private long _lastRequest;
    private int _repairVersion;
    private int _repairedVersion;
    private int _disposed;

    public event Action<Exception>? Error;
    public Task Completion => _worker;

    public StartMenuWatcher(IEnumerable<string> roots, Func<CancellationToken, Task> refresh,
        CancellationToken lifetime = default, TimeSpan? debounce = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(refresh);
        _roots = roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _refresh = refresh;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(700);
        if (_debounce < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(debounce));
        _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        RepairWatchers();
        _worker = Task.Run(PumpAsync);
    }

    public void RequestRefresh()
    {
        if (Volatile.Read(ref _disposed) != 0 || _stop.IsCancellationRequested) return;
        Interlocked.Exchange(ref _lastRequest, Stopwatch.GetTimestamp());
        _requests.Writer.TryWrite(true);
    }

    private async Task PumpAsync()
    {
        try
        {
            var token = _stop.Token;
            while (await _requests.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (_requests.Reader.TryRead(out _)) { }
                // Events update a timestamp instead of scheduling one Task or scan per event.
                while (true)
                {
                    var remaining = _debounce - Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastRequest));
                    if (remaining <= TimeSpan.Zero) break;
                    await Task.Delay(remaining, token).ConfigureAwait(false);
                }
                // All changes already included in this refresh can be removed from the signal queue.
                while (_requests.Reader.TryRead(out _)) { }
                token.ThrowIfCancellationRequested();
                RepairWatchers();
                try { await _refresh(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) { Report(ex); }
                // Changes arriving during a refresh remain queued for one subsequent refresh.
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally { StopWatching(); }
    }

    private void RepairWatchers()
    {
        lock (_watchGate)
        {
            if (_stop.IsCancellationRequested || Volatile.Read(ref _disposed) != 0) return;
            var repairVersion = Volatile.Read(ref _repairVersion);
            var force = repairVersion != _repairedVersion;
            foreach (var root in _roots)
            {
                // Watching an existing ancestor catches creation of a missing Programs directory.
                var watchPath = FindExistingAncestor(root);
                if (_watchers.TryGetValue(root, out var current))
                {
                    if (!force && string.Equals(current.Path, watchPath, StringComparison.OrdinalIgnoreCase)) continue;
                    current.Watcher.Dispose();
                    _watchers.Remove(root);
                }
                if (watchPath is null) continue;
                FileSystemWatcher? watcher = null;
                try
                {
                    watcher = new FileSystemWatcher(watchPath)
                    {
                        Filter = "*",
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                        InternalBufferSize = 32 * 1024
                    };
                    watcher.Created += (_, e) => Changed(root, e.FullPath, false);
                    watcher.Changed += (_, e) => Changed(root, e.FullPath, true);
                    watcher.Deleted += (_, e) => Changed(root, e.FullPath, false);
                    watcher.Renamed += (_, e) =>
                    {
                        Changed(root, e.OldFullPath, false);
                        Changed(root, e.FullPath, false);
                    };
                    watcher.Error += (_, e) =>
                    {
                        Interlocked.Increment(ref _repairVersion);
                        Report(e.GetException());
                        RequestRefresh();
                    };
                    watcher.EnableRaisingEvents = true;
                    _watchers[root] = new Registration(watchPath, watcher);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    watcher?.Dispose();
                    Report(ex);
                }
            }
            _repairedVersion = repairVersion;
        }
    }

    private void Changed(string root, string path, bool contentOnly)
    {
        if (!Related(root, path)) return;
        if (contentOnly && !string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase)) return;
        RequestRefresh();
    }

    private static bool Related(string root, string path) =>
        IsAtOrBelow(path, root) || IsAtOrBelow(root, path);

    private static bool IsAtOrBelow(string path, string parent)
    {
        if (string.Equals(path, parent, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindExistingAncestor(string path)
    {
        var candidate = path;
        while (!Directory.Exists(candidate))
        {
            var parent = Path.GetDirectoryName(candidate);
            if (string.IsNullOrEmpty(parent) || parent == candidate) return null;
            candidate = parent;
        }
        return candidate;
    }

    private void Report(Exception exception)
    {
        try { Error?.Invoke(exception); }
        catch { /* Diagnostics subscribers must not terminate the watcher. */ }
    }

    private void StopWatching()
    {
        lock (_watchGate)
        {
            foreach (var registration in _watchers.Values) registration.Watcher.Dispose();
            _watchers.Clear();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _requests.Writer.TryComplete();
        _stop.Cancel();
        StopWatching();
        _ = _worker.ContinueWith(_ => _stop.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private sealed record Registration(string Path, FileSystemWatcher Watcher);
}
