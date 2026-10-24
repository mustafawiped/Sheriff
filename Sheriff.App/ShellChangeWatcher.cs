using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Sheriff.App;

/// <summary>Watches the virtual AppsFolder; Shell work never runs in the message hook.</summary>
public sealed class ShellChangeWatcher : IDisposable
{
    private const int NotificationMessage = 0x8000 + 0x362;
    private const int ShellLevel = 0x0002, InterruptLevel = 0x0001;
    private const int ChangeEvents = 0x00000001 | 0x00000002 | 0x00000004 | 0x00000008 | 0x00000010 | 0x00001000 | 0x00002000 | 0x00020000 | 0x08000000;
    private readonly nint _hwnd;
    private readonly Action _changed;
    private readonly HwndSource? _source;
    private readonly object _gate = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint _registration;
    private nint _pidl;
    private volatile bool _disposed;

    public bool IsRegistered => Volatile.Read(ref _registration) != 0;
    public Task<bool> Ready => _ready.Task;

    // Construct and dispose on the WPF window's dispatcher thread.
    public ShellChangeWatcher(nint hwnd, Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        _hwnd = hwnd;
        _changed = changed;
        _source = HwndSource.FromHwnd(hwnd);
        if (_source is null)
        {
            Diagnostics.Write("SHELL_WATCH HWND has no WPF source");
            _ready.TrySetResult(false);
            return;
        }
        _source.AddHook(WindowMessage);
        new Thread(RegisterOnBackgroundThread) { IsBackground = true, Name = "Sheriff AppsFolder notifications" }.Start();
    }

    private void RegisterOnBackgroundThread()
    {
        nint parsedPidl = 0;
        var initialized = false;
        try
        {
            var comResult = CoInitializeEx(0, 2); // Dedicated STA for Shell namespace extensions.
            if (comResult < 0) { Diagnostics.Write($"SHELL_WATCH COM initialization failed 0x{comResult:X8}"); return; }
            initialized = true;
            var result = SHParseDisplayName("shell:AppsFolder", 0, out parsedPidl, 0, out _);
            if (result < 0 || parsedPidl == 0) { Diagnostics.Write($"SHELL_WATCH AppsFolder parsing failed 0x{result:X8}"); return; }
            if (_source!.Dispatcher.HasShutdownStarted) return;
            // Keep legacy Shell delivery on the window's long-lived message-pump thread.
            _source.Dispatcher.Invoke(() =>
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    var entry = new ShellChangeNotifyEntry { Pidl = parsedPidl, Recursive = true };
                    // Legacy delivery is deliberate: no payload is read or retained, so no shared-memory lock is needed.
                    _registration = SHChangeNotifyRegister(_hwnd, ShellLevel | InterruptLevel, ChangeEvents, NotificationMessage, 1, ref entry);
                    if (_registration == 0) { Diagnostics.Write("SHELL_WATCH registration returned zero"); return; }
                    _pidl = parsedPidl;
                    parsedPidl = 0;
                    Diagnostics.Write("SHELL_WATCH registered AppsFolder");
                }
            });
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException or InvalidOperationException or TaskCanceledException)
        {
            Diagnostics.Write("SHELL_WATCH " + ex.Message);
        }
        finally
        {
            if (parsedPidl != 0) Marshal.FreeCoTaskMem(parsedPidl);
            if (initialized) CoUninitialize();
            _ready.TrySetResult(IsRegistered);
        }
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != NotificationMessage || _disposed) return 0;
        handled = true;
        try { _changed(); }
        catch (Exception ex) { Diagnostics.Write("SHELL_WATCH callback " + ex.Message); }
        return 0;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_registration != 0 && !SHChangeNotifyDeregister(_registration)) Diagnostics.Write("SHELL_WATCH deregistration returned false");
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException) { Diagnostics.Write("SHELL_WATCH dispose " + ex.Message); }
            finally
            {
                _registration = 0;
                if (_pidl != 0) { Marshal.FreeCoTaskMem(_pidl); _pidl = 0; }
            }
        }
        if (_source is not null)
        {
            if (_source.Dispatcher.CheckAccess()) _source.RemoveHook(WindowMessage);
            else if (!_source.Dispatcher.HasShutdownStarted) _source.Dispatcher.Invoke(() => _source.RemoveHook(WindowMessage));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShellChangeNotifyEntry
    {
        public nint Pidl;
        [MarshalAs(UnmanagedType.Bool)] public bool Recursive;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHParseDisplayName(string name, nint bindContext, out nint pidl, uint requestedAttributes, out uint attributes);
    [DllImport("shell32.dll")]
    private static extern uint SHChangeNotifyRegister(nint hwnd, int sources, int events, uint message, int count, ref ShellChangeNotifyEntry entry);
    [DllImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHChangeNotifyDeregister(uint registration);
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint concurrency);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}
