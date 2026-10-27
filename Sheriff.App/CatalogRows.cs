using System.ComponentModel;
using System.Windows.Media;
using Sheriff.Core;

namespace Sheriff.App;

public sealed class AppTile(AppEntry entry, ShellIconCache? icons, bool pinned) : INotifyPropertyChanged
{
    private bool _selected;
    private bool _showHoverActions;
    public AppEntry Entry { get; } = entry;
    public ImageSource? Icon => icons?.GetImage(Entry.IconCachePath);
    public bool IsPinned { get; } = pinned;
    public string Initial => string.IsNullOrWhiteSpace(Entry.DisplayName) ? "?" : Entry.DisplayName.Trim()[..1].ToUpperInvariant();
    public string Tooltip => string.IsNullOrWhiteSpace(Entry.Publisher) ? Entry.DisplayName : Entry.DisplayName + "\n" + Entry.Publisher;
    public bool IsSelected { get => _selected; set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    public bool ShowHoverActions { get => _showHoverActions; set { if (_showHoverActions == value) return; _showHoverActions = value; PropertyChanged?.Invoke(this, new(nameof(ShowHoverActions))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
public sealed record CatalogHeader(string Name, int Count, bool Collapsed, string? Label = null)
{
    public string DisplayName => Label ?? Name;
    public string Chevron => Collapsed ? "\uE70D" : "\uE70E";
    public string AccessibleName => $"{DisplayName}, {Count} uygulama, {(Collapsed ? "genişlet" : "daralt")}";
}
public sealed record CatalogTileRow(IReadOnlyList<AppTile> Tiles)
{
    public string Category => Tiles.FirstOrDefault()?.Entry.Category ?? Categories.Other;
}
public sealed record CatalogCategoryCard(string Name, int Count, IReadOnlyList<AppTile> PreviewTiles, string? Label = null, string? ColorHex = null)
{
    public string DisplayName => Label ?? Name;
    public string AccessibleName => $"{DisplayName}, {Count} uygulama";
    public string OverflowAccessibleName => $"{DisplayName} kategorisindeki tüm uygulamaları aç";
    public IReadOnlyList<AppTile> PrimaryPreviewTiles { get; } = PreviewTiles.Take(3).ToArray();
    public IReadOnlyList<AppTile> OverflowPreviewTiles { get; } = PreviewTiles.Skip(3).Take(4).ToArray();
    public bool HasOverflowPreviewTiles => OverflowPreviewTiles.Count > 0;
    public bool HasNoPreviewTiles => PreviewTiles.Count == 0;
    public Brush SurfaceBrush { get; } = CreateSurfaceBrush(ColorHex);
    public Brush OutlineBrush { get; } = CreateOutlineBrush(ColorHex);

    internal static Brush CreateSurfaceBrush(string? colorHex)
    {
        if (System.Windows.SystemParameters.HighContrast) return System.Windows.SystemColors.ControlBrush;
        var dark = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() != Wpf.Ui.Appearance.ApplicationTheme.Light;
        var neutral = dark ? (byte)48 : (byte)240;
        var color = Color.FromRgb(neutral, neutral, neutral);
        if (colorHex is not null && ColorConverter.ConvertFromString(colorHex) is Color accent)
            color = Color.FromRgb((byte)(neutral * 0.84 + accent.R * 0.16), (byte)(neutral * 0.84 + accent.G * 0.16), (byte)(neutral * 0.84 + accent.B * 0.16));
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    internal static Brush CreateOutlineBrush(string? colorHex)
    {
        if (System.Windows.SystemParameters.HighContrast) return System.Windows.SystemColors.ControlTextBrush;
        var color = colorHex is not null && ColorConverter.ConvertFromString(colorHex) is Color accent
            ? Color.FromArgb(90, accent.R, accent.G, accent.B) : Color.FromArgb(20, 0, 0, 0);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
public sealed record CatalogCategoryRow(IReadOnlyList<CatalogCategoryCard> Cards);
internal sealed record AppDragPayload(string AppId, Guid Session);
