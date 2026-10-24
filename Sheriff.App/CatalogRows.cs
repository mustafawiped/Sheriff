using System.ComponentModel;
using System.Windows.Media;
using Sheriff.Core;

namespace Sheriff.App;

public sealed class AppTile(AppEntry entry, ImageSource? icon, bool pinned) : INotifyPropertyChanged
{
    private bool _selected;
    private bool _showHoverActions;
    public AppEntry Entry { get; } = entry;
    public ImageSource? Icon { get; } = icon;
    public bool IsPinned { get; } = pinned;
    public string Initial => string.IsNullOrWhiteSpace(Entry.DisplayName) ? "?" : Entry.DisplayName.Trim()[..1].ToUpperInvariant();
    public string Tooltip => string.IsNullOrWhiteSpace(Entry.Publisher) ? Entry.DisplayName : Entry.DisplayName + "\n" + Entry.Publisher;
    public bool IsSelected { get => _selected; set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    public bool ShowHoverActions { get => _showHoverActions; set { if (_showHoverActions == value) return; _showHoverActions = value; PropertyChanged?.Invoke(this, new(nameof(ShowHoverActions))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
public sealed record CatalogHeader(string Name, int Count, bool Collapsed)
{
    public string Chevron => Collapsed ? "\uE70D" : "\uE70E";
    public string AccessibleName => $"{Name}, {Count} uygulama, {(Collapsed ? "genişlet" : "daralt")}";
}
public sealed record CatalogTileRow(IReadOnlyList<AppTile> Tiles)
{
    public string Category => Tiles.FirstOrDefault()?.Entry.Category ?? Categories.Other;
}
public sealed record CatalogCategoryCard(string Name, int Count, IReadOnlyList<AppTile> PreviewTiles)
{
    public string AccessibleName => $"{Name}, {Count} uygulama";
    public bool HasNoPreviewTiles => PreviewTiles.Count == 0;
}
public sealed record CatalogCategoryRow(IReadOnlyList<CatalogCategoryCard> Cards);
internal sealed record AppDragPayload(string AppId, Guid Session);
