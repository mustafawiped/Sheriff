using Sheriff.Core.Scanning;
using Xunit;

namespace Sheriff.Tests;

public sealed class StartMenuWatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Sheriff-watcher-" + Guid.NewGuid().ToString("N"));

    public StartMenuWatcherTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ARequestBurstProducesOneRefresh()
    {
        var calls = 0;
        using var watcher = new StartMenuWatcher([_root], _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        }, debounce: TimeSpan.FromMilliseconds(100));
        for (var i = 0; i < 100; i++) watcher.RequestRefresh();
        await WaitForAsync(() => Volatile.Read(ref calls) >= 1);
        await Task.Delay(200);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task ShortcutCreateRenameAndDeleteAreDetected()
    {
        var calls = 0;
        using var watcher = new StartMenuWatcher([_root], _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        }, debounce: TimeSpan.FromMilliseconds(60));

        var first = Path.Combine(_root, "Example.lnk");
        await File.WriteAllTextAsync(first, "watcher fixture");
        await WaitForAsync(() => Volatile.Read(ref calls) >= 1);
        await Task.Delay(120);
        var beforeRename = Volatile.Read(ref calls);
        var second = Path.Combine(_root, "Renamed.lnk");
        File.Move(first, second);
        await WaitForAsync(() => Volatile.Read(ref calls) > beforeRename);
        await Task.Delay(120);
        var beforeDelete = Volatile.Read(ref calls);
        File.Delete(second);
        await WaitForAsync(() => Volatile.Read(ref calls) > beforeDelete);
        Assert.False(File.Exists(second));
    }

    [Fact]
    public async Task DirectoryCreatedAfterWatchingAMissingRootIsDetected()
    {
        var missingRoot = Path.Combine(_root, "New", "Programs");
        var sawShortcut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new StartMenuWatcher([missingRoot], _ =>
        {
            if (File.Exists(Path.Combine(missingRoot, "App.lnk"))) sawShortcut.TrySetResult();
            return Task.CompletedTask;
        }, debounce: TimeSpan.FromMilliseconds(60));
        Directory.CreateDirectory(missingRoot);
        await File.WriteAllTextAsync(Path.Combine(missingRoot, "App.lnk"), "fixture");
        await sawShortcut.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ChangesDuringRefreshAreCoalescedAndNeverRunConcurrently()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var running = 0;
        var maximumRunning = 0;
        using var watcher = new StartMenuWatcher([_root], async token =>
        {
            var active = Interlocked.Increment(ref running);
            maximumRunning = Math.Max(maximumRunning, active);
            var call = Interlocked.Increment(ref calls);
            try
            {
                if (call == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(token);
                }
            }
            finally { Interlocked.Decrement(ref running); }
        }, debounce: TimeSpan.FromMilliseconds(40));
        watcher.RequestRefresh();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 50; i++) watcher.RequestRefresh();
        releaseFirst.SetResult();
        await WaitForAsync(() => Volatile.Read(ref calls) >= 2);
        await Task.Delay(150);
        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.Equal(1, maximumRunning);
    }

    [Fact]
    public async Task DisposeCancelsActiveRefreshAndPreventsFurtherCallbacks()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var watcher = new StartMenuWatcher([_root], async token =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, debounce: TimeSpan.Zero);
        watcher.RequestRefresh();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        watcher.Dispose();
        await watcher.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        watcher.RequestRefresh();
        await File.WriteAllTextAsync(Path.Combine(_root, "AfterDispose.lnk"), "fixture");
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref calls));
        watcher.Dispose();
    }

    [Fact]
    public async Task RefreshFailureDoesNotDisableLaterRefreshes()
    {
        var calls = 0;
        var errors = 0;
        using var watcher = new StartMenuWatcher([_root], _ =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new IOException("Temporary fixture failure");
            return Task.CompletedTask;
        }, debounce: TimeSpan.FromMilliseconds(30));
        watcher.Error += _ => Interlocked.Increment(ref errors);
        watcher.RequestRefresh();
        await WaitForAsync(() => Volatile.Read(ref errors) == 1);
        watcher.RequestRefresh();
        await WaitForAsync(() => Volatile.Read(ref calls) == 2);
        Assert.Equal(1, Volatile.Read(ref errors));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
