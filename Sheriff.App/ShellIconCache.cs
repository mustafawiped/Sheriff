using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sheriff.Core;
using Sheriff.Core.Scanning;

namespace Sheriff.App;

/// <summary>GetOrCreate is used by the scanner; GetImage is memory-only for the UI.</summary>
public sealed class ShellIconCache(string iconsDirectory) : IAppIconCache
{
    private readonly ConcurrentDictionary<string, BitmapSource> _images = new(StringComparer.OrdinalIgnoreCase);
    public int ExtractedCount { get; private set; }
    public int DiskHitCount { get; private set; }
    public int MemoryHitCount { get; private set; }

    public ImageSource? GetImage(string path) => _images.TryGetValue(path, out var image) ? image : null;

    public void WarmImages(IEnumerable<AppEntry> apps)
    {
        foreach (var app in apps)
            if (!string.IsNullOrEmpty(app.IconCachePath)) TryLoad(app.IconCachePath);
    }

    public string GetOrCreate(AppEntry app, string parsingName, string iconLocation, DateTimeOffset modifiedAt)
    {
        Directory.CreateDirectory(iconsDirectory);
        var targetStamp = app.Source == AppSource.StartMenu && File.Exists(app.TargetPathOrAumid)
            ? File.GetLastWriteTimeUtc(app.TargetPathOrAumid).Ticks : 0;
        var stamp = $"{app.Id}\n{parsingName}\n{iconLocation}\n{modifiedAt.UtcTicks}\n{targetStamp}";
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp)))[..16].ToLowerInvariant();
        var path = Path.Combine(iconsDirectory, app.Id + "-" + fingerprint + ".png");
        if (_images.ContainsKey(path)) { MemoryHitCount++; return path; }
        if (TryLoad(path)) { DiskHitCount++; return path; }
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
            _images[path] = bitmap;
            ExtractedCount++;
            return path;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private bool TryLoad(string path)
    {
        if (_images.ContainsKey(path)) return true;
        try
        {
            if (!File.Exists(path)) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var image = decoder.Frames[0];
            image.Freeze();
            _images[path] = image;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException) { return false; }
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
            // ICONONLY | BIGGERSIZEOK. This API can block, so only the scanner calls it.
            if (factory.GetImage(new SizeInt { Width = 64, Height = 64 }, 0x5, out bitmap) < 0 || bitmap == 0) return null;
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
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
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SizeInt size, uint flags, out nint bitmap);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)] private static extern int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
}
