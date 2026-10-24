using System.Collections.Concurrent;
using System.IO;

namespace Sheriff.App;

internal static class Diagnostics
{
    private static readonly ConcurrentQueue<string> Pending = new();
    private static readonly SemaphoreSlim Writer = new(1, 1);
    internal static string Root => Environment.GetEnvironmentVariable("SHERIFF_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sheriff");
    internal static void Write(string line)
    {
        Pending.Enqueue($"{DateTimeOffset.Now:O} {line}");
        _ = Task.Run(Flush);
    }
    internal static void WriteTiming(string phase, double milliseconds, nint handle)
    {
        var marker = milliseconds >= 50 ? " OVER_TARGET" : "";
        Write(FormattableString.Invariant($"{phase} {milliseconds:F2} ms hwnd={handle}{marker}"));
    }
    internal static void Flush()
    {
        Writer.Wait();
        try
        {
            Directory.CreateDirectory(Path.Combine(Root, "logs"));
            var lines = new List<string>();
            while (Pending.TryDequeue(out var line)) lines.Add(line);
            if (lines.Count > 0) File.AppendAllLines(Path.Combine(Root, "logs", "diagnostics.log"), lines);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { Writer.Release(); }
    }
}
