using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sheriff.App;

internal static class Program
{
    internal static string PipeName { get; private set; } = "";

    [STAThread]
    private static int Main(string[] args)
    {
        ProcessIdToSessionId(GetCurrentProcessId(), out var session);
        PipeName = $"Sheriff-v1-{Environment.UserName}-{session}";
        if (Environment.GetEnvironmentVariable("SHERIFF_TEST_INSTANCE") is { Length: > 0 } testInstance)
            PipeName += "-" + SanitizeTestName(testInstance);
        using var singleton = new Mutex(true, @"Local\" + PipeName, out var created);
        var exiting = Array.IndexOf(args, "--exit") >= 0;
        if (!created || exiting)
        {
            AllowSetForegroundWindow(-1);
            return SendCommand(exiting ? "EXIT" : "TOGGLE") ? 0 : 2;
        }
        try { return RunPrimary(args); }
        finally { singleton.ReleaseMutex(); }
    }

    private static string SanitizeTestName(string value)
    {
        var buffer = new char[value.Length];
        var count = 0;
        foreach (var character in value) if (char.IsLetterOrDigit(character)) buffer[count++] = character;
        return new string(buffer, 0, count);
    }

    // Keep WPF and the catalog dependencies out of the second-instance JIT path.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunPrimary(string[] args) => new App(args).Run();

    internal static bool SendCommand(string command)
    {
        // The first process can own the mutex while WPF is still warming up.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                pipe.Connect(150);
                using var writer = new StreamWriter(pipe) { AutoFlush = true };
                writer.WriteLine(command);
                return true;
            }
            catch (IOException) { }
            catch (TimeoutException) { }
            catch (UnauthorizedAccessException) { return false; }
        }
        return false;
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
}
