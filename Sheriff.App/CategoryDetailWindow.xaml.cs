using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Sheriff.Core;

namespace Sheriff.App;

public partial class CategoryDetailWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly string _categoryId;
    private readonly ShellIconCache _icons;
    private readonly DispatcherTimer _filterTimer;
    private IReadOnlyList<AppTile> _categoryApps = [];
    private SheriffConfig _config;
    private bool _ready;
    public AppEntry? LaunchEntry { get; private set; }
    public bool LaunchAsAdministrator { get; private set; }
    public event Action<AppEntry>? PinRequested;
    public event Action<AppEntry>? HideRequested;
    public event Action<AppEntry>? LocationRequested;
    public event Action<AppEntry, string>? CategoryChangeRequested;
    public event Action<AppEntry>? CategoryResetRequested;

    public CategoryDetailWindow(string categoryId, SheriffConfig config, IReadOnlyList<AppEntry> apps, ShellIconCache icons)
    {
        InitializeComponent();
        _categoryId = categoryId;
        _config = config;
        _icons = icons;
        _filterTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); RebuildRows(); };
        _ready = true;
        CategoryTitle.Text = CategoryCustomization.GetDisplayName(config, categoryId);
        Title = CategoryTitle.Text;
        if (CategoryCustomization.GetColor(config, categoryId) is { } color)
        {
            WindowSurface.Background = CatalogCategoryCard.CreateSurfaceBrush(color);
            WindowSurface.BorderBrush = CatalogCategoryCard.CreateOutlineBrush(color);
        }
        UpdateApps(apps, config);
    }

    public void UpdateApps(IReadOnlyList<AppEntry> apps, SheriffConfig config)
    {
        _config = config;
        _categoryApps = apps
            .Where(app => app.Category.Equals(_categoryId, StringComparison.OrdinalIgnoreCase)
                && !config.HiddenAppIds.Contains(app.Id) && !AutoBlockPolicy.IsBlocked(app, config))
            .Select(app => new AppTile(app, _icons, config.PinnedAppIds.Contains(app.Id)))
            .ToArray();
        CategoryTitle.Text = CategoryCustomization.GetDisplayName(config, _categoryId);
        Title = CategoryTitle.Text;
        CategoryCount.Text = $"{_categoryApps.Count} uygulama";
        RebuildRows();
    }

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private void RebuildRows()
    {
        var query = SearchBox.Text.Trim();
        var filtered = query.Length == 0 ? _categoryApps.ToArray() : _categoryApps.Where(tile =>
            Matches(tile.Entry.DisplayName, query) || Matches(tile.Entry.Publisher, query)).ToArray();
        var rows = filtered.Chunk(4).Select(row => new CatalogTileRow(row)).ToArray();
        AppRows.ItemsSource = rows;
        EmptyState.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        FooterText.Text = query.Length == 0
            ? $"{_categoryApps.Count} uygulama · Açmak için tıklayın"
            : $"{filtered.Length} sonuç · Açmak için tıklayın";
    }

    private static bool Matches(string text, string query) => CultureInfo.CurrentCulture.CompareInfo.IndexOf(text, query,
        CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    private void LaunchClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AppTile tile }) RequestLaunch(tile.Entry, false);
    }

    private void AppContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Button { DataContext: AppTile tile } button) return;
        var menu = button.ContextMenu ??= new ContextMenu();
        menu.Items.Clear();
        var category = new MenuItem { Header = "Kategori değiştir" };
        foreach (var id in _config.Categories)
        {
            var item = new MenuItem
            {
                Header = CategoryCustomization.GetDisplayName(_config, id),
                IsCheckable = true,
                IsChecked = string.Equals(tile.Entry.Category, id, StringComparison.OrdinalIgnoreCase)
            };
            item.Click += (_, _) => CategoryChangeRequested?.Invoke(tile.Entry, id);
            category.Items.Add(item);
        }
        category.Items.Add(new Separator());
        var reset = new MenuItem { Header = "Otomatik kategoriyi geri yükle", IsEnabled = _config.CategoryOverrides.ContainsKey(tile.Entry.Id) };
        reset.Click += (_, _) => CategoryResetRequested?.Invoke(tile.Entry);
        category.Items.Add(reset);
        menu.Items.Add(category);
        AddMenu(menu, tile.IsPinned ? "Sabitlemeyi kaldır" : "Sabitle", () => PinRequested?.Invoke(tile.Entry));
        AddMenu(menu, "Gizle", () => HideRequested?.Invoke(tile.Entry));
        menu.Items.Add(new Separator());
        AddMenu(menu, "Dosya konumunu aç", () => LocationRequested?.Invoke(tile.Entry));
        var admin = new MenuItem { Header = "Yönetici olarak çalıştır", IsEnabled = tile.Entry.Source != AppSource.Store };
        admin.Click += (_, _) => RequestLaunch(tile.Entry, true);
        if (!admin.IsEnabled)
        {
            admin.ToolTip = "Store uygulamaları Windows üzerinden normal kullanıcı olarak başlatılır.";
            ToolTipService.SetShowOnDisabled(admin, true);
        }
        menu.Items.Add(admin);
    }

    private static void AddMenu(ContextMenu menu, string text, Action action)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void RequestLaunch(AppEntry app, bool asAdministrator)
    {
        LaunchEntry = app;
        LaunchAsAdministrator = asAdministrator;
        DialogResult = true;
    }

    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
    }

    protected override void OnClosed(EventArgs e)
    {
        _filterTimer.Stop();
        base.OnClosed(e);
    }
}
