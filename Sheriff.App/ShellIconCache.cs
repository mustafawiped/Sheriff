using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sheriff.Core;
using Sheriff.Core.Scanning;

namespace Sheriff.App;

/// <summary>The scanner stores icon files; the UI decodes only requested icons into a bounded LRU cache.</summary>
public sealed class ShellIconCache(string iconsDirectory) : IAppIconCache
{
    private const int MaxInMemoryImages = 48;
    private const int MaxIconDecodeWidth = 64;
    private readonly object _imageGate = new();
    private readonly Dictionary<string, LinkedListNode<CachedIcon>> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<CachedIcon> _imageLru = new();
    private int _extractedCount, _diskHitCount, _memoryHitCount;
    public int ExtractedCount => Volatile.Read(ref _extractedCount);
    public int DiskHitCount => Volatile.Read(ref _diskHitCount);
    public int MemoryHitCount => Volatile.Read(ref _memoryHitCount);

    public ImageSource? GetImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        lock (_imageGate)
        {
            if (_images.TryGetValue(path, out var cached))
            {
                _imageLru.Remove(cached);
                _imageLru.AddFirst(cached);
                Interlocked.Increment(ref _memoryHitCount);
                return cached.Value.Image;
            }

            var image = TryLoad(path);
            if (image is null) return null;
            Interlocked.Increment(ref _diskHitCount);
            var node = _imageLru.AddFirst(new CachedIcon(path, image));
            _images.Add(path, node);
            if (_images.Count > MaxInMemoryImages)
            {
                var oldest = _imageLru.Last!;
                _imageLru.RemoveLast();
                _images.Remove(oldest.Value.Path);
            }
            return image;
        }
    }

    public string GetOrCreate(AppEntry app, string parsingName, string iconLocation, DateTimeOffset modifiedAt)
    {
        Directory.CreateDirectory(iconsDirectory);
        var targetStamp = app.Source == AppSource.StartMenu && File.Exists(app.TargetPathOrAumid)
            ? File.GetLastWriteTimeUtc(app.TargetPathOrAumid).Ticks : 0;
        var stamp = $"{app.Id}\n{parsingName}\n{iconLocation}\n{modifiedAt.UtcTicks}\n{targetStamp}";
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp)))[..16].ToLowerInvariant();
        var path = Path.Combine(iconsDirectory, app.Id + "-" + fingerprint + ".png");
        if (File.Exists(path)) { Interlocked.Increment(ref _diskHitCount); return path; }
        BitmapSource? bitmap = null;
        var candidates = new List<string> { parsingName };
        if (!string.IsNullOrWhiteSpace(iconLocation))
        {
            var comma = iconLocation.LastIndexOf(',');
            var iconPath = (comma >= 0 ? iconLocation[..comma] : iconLocation).Trim().Trim('"');
            candidates.Add(Environment.ExpandEnvironmentVariables(iconPath));
        }
        if (app.Source == AppSource.StartMenu) candidates.Add(app.TargetPathOrAumid);
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bitmap = Extract(candidate);
            if (bitmap is not null) break;
        }
        if (bitmap is null) return "";
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) encoder.Save(output);
            File.Move(temporary, path, true);
            Interlocked.Increment(ref _extractedCount);
            return path;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static BitmapSource? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = MaxIconDecodeWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException) { return null; }
    }

    private static BitmapSource? Extract(string parsingName)
    {
        if (string.IsNullOrWhiteSpace(parsingName)) return null;
        IShellItemImageFactory? factory = null;
        nint bitmap = 0;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(parsingName, 0, ref iid, out factory) < 0 || factory is null) return null;
            // Request a standard-size icon without the larger-image option; only the scanner calls this blocking API.
            if (factory.GetImage(new SizeInt { Width = MaxIconDecodeWidth, Height = MaxIconDecodeWidth }, 0x4, out bitmap) < 0 || bitmap == 0) return null;
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(MaxIconDecodeWidth, MaxIconDecodeWidth));
            image.Freeze();
            return image;
        }
        catch (COMException) { return null; }
        finally
        {
            if (bitmap != 0) DeleteObject(bitmap);
            if (factory is not null) Marshal.FinalReleaseComObject(factory);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SizeInt { public int Width, Height; }
    private sealed record CachedIcon(string Path, BitmapSource Image);
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SizeInt size, uint flags, out nint bitmap);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)] private static extern int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
}
