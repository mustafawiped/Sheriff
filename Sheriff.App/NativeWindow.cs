using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace Sheriff.App;

internal static class NativeWindow
{
    private const int HotkeyId = 0x5348;
    private static uint _activeModifiers, _activeKey;
    public static void ConfigureToolWindow(nint hwnd)
    {
        var style = GetWindowLongPtr(hwnd, -20).ToInt64();
        var testing = Environment.GetEnvironmentVariable("SHERIFF_UI_TEST") == "1";
        SetWindowLongPtr(hwnd, -20, new nint(testing ? (style | 0x40000L) & ~0x80L : (style | 0x80L) & ~0x40000L));
    }

    public static bool RegisterShortcut(nint hwnd, string shortcut)
    {
        if (!TryParseShortcut(shortcut, out var modifiers, out var key)) return false;
        if (modifiers == _activeModifiers && key == _activeKey) return true;
        UnregisterHotKey(hwnd, HotkeyId);
        if (RegisterHotKey(hwnd, HotkeyId, modifiers | 0x4000, key)) { _activeModifiers = modifiers; _activeKey = key; return true; }
        if (_activeKey != 0) RegisterHotKey(hwnd, HotkeyId, _activeModifiers | 0x4000, _activeKey);
        return false;
    }
    private static bool TryParseShortcut(string shortcut, out uint modifiers, out uint key)
    {
        modifiers = 0; key = 0;
        foreach (var token in shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (token.ToUpperInvariant())
            {
                case "CTRL": case "CONTROL": modifiers |= 2; break;
                case "ALT": modifiers |= 1; break;
                case "SHIFT": modifiers |= 4; break;
                case "WIN": modifiers |= 8; break;
                default:
                    if (key != 0) return false;
                    if (!Enum.TryParse<Key>(token, true, out var parsed)) return false;
                    key = (uint)KeyInterop.VirtualKeyFromKey(parsed); break;
            }
        }
        return modifiers != 0 && key != 0;
    }
    public static void UnregisterShortcut(nint hwnd) => UnregisterHotKey(hwnd, HotkeyId);
    public static void Activate(nint hwnd) => SetForegroundWindow(hwnd);

    public static bool ApplyAppearance(nint hwnd, bool dark, bool highContrast)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return false;
        var darkMode = dark ? 1 : 0;
        var corners = highContrast ? 1 : 2; // DWMWCP_DONOTROUND / DWMWCP_ROUND
        _ = DwmSetWindowAttribute(hwnd, 20, ref darkMode, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, 33, ref corners, sizeof(int));
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)) return false;
        var backdrop = highContrast ? 1 : 2; // DWMSBT_NONE / DWMSBT_MAINWINDOW (Mica)
        return DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int)) >= 0 && !highContrast;
    }

    public static void Place(Window window, nint hwnd)
    {
        GetCursorPos(out var cursor);
        var monitor = MonitorFromPoint(cursor, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var dpi = 96u;
        try { if (GetDpiForMonitor(monitor, 0, out var x, out _) == 0) dpi = x; } catch (DllNotFoundException) { }
        var scale = dpi / 96d;
        var width = Math.Min(688, (info.Work.Right - info.Work.Left) / scale - 24);
        var height = Math.Min(680, (info.Work.Bottom - info.Work.Top) / scale - 24);
        window.Width = Math.Max(320, width); window.Height = Math.Max(280, height);
        var left = info.Work.Left + ((info.Work.Right - info.Work.Left) - window.Width * scale) / 2;
        var top = info.Work.Bottom - window.Height * scale - 12 * scale;
        if (info.Work.Top > info.Monitor.Top) top = info.Work.Top + 12 * scale;
        if (info.Work.Left > info.Monitor.Left) left = info.Work.Left + 12 * scale;
        else if (info.Work.Right < info.Monitor.Right) left = info.Work.Right - window.Width * scale - 12 * scale;
        // Physical coordinates avoid mixed-DPI desktop-origin conversion errors.
        SetWindowPos(hwnd, new nint(-1), (int)left, (int)top, (int)(window.Width * scale), (int)(window.Height * scale), 0x0010);
    }

    public static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("Sheriff", $"\"{Environment.ProcessPath}\" --background");
        else key.DeleteValue("Sheriff", false);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
