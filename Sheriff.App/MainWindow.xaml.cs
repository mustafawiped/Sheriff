using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Sheriff.Core;
using Wpf.Ui.Appearance;

namespace Sheriff.App;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow, IDisposable
{
    private HwndSource? _source;
    private bool _warming, _viewReady, _hiding;
    private long _lastDeactivated;
    private EventHandler? _rendered;
    private CatalogService? _catalog;
    private IReadOnlyList<AppEntry> _apps = [];
    private readonly Dictionary<string, AppTile> _tiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase) { Categories.Junk };
    private readonly List<object> _rows = [];
    private readonly List<AppTile> _visibleTiles = [];
    private readonly Dictionary<AppTile, object> _tileRows = [];
    private readonly DispatcherTimer _hoverTimer;
    private readonly DispatcherTimer _animationCacheReleaseTimer;
    private AppTile? _hoverCandidate;
    private string? _activeCategory;
    private int _displayedAppCount, _categoryCardCount;
    private int _selection = -1;
    private readonly Guid _dragSession = Guid.NewGuid();
    private Point _dragStart;
    private AppTile? _dragTile;
    private bool _dragging, _suppressClick;
    private long _lastDragScroll;
    private FrameworkElement? _dropHighlight;
    private Brush? _dropBackground, _dropBorder;
    private readonly DoubleAnimation _contentFade = new(0.92, 1, TimeSpan.FromMilliseconds(130)) { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
    private readonly DoubleAnimation _contentSlide = new(6, 0, TimeSpan.FromMilliseconds(130)) { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
    private readonly DoubleAnimation _contentScaleX = new(0.975, 1, TimeSpan.FromMilliseconds(130)) { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
    private readonly DoubleAnimation _contentScaleY = new(0.975, 1, TimeSpan.FromMilliseconds(130)) { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
    private long _showStarted;
    private bool _animateContent;
    public nint Handle { get; private set; }
    public bool HotkeyRegistered { get; private set; }
    public bool SuppressAutoHide { get; set; }
    public int VisibleAppCount => _displayedAppCount;
    public int CatalogRowCount => _rows.Count;
    public bool CategoryOverviewVisible => _activeCategory is null;
    public int CategoryCardCount => _categoryCardCount;
    public double LastShowRenderMs { get; private set; } = double.NaN;
    public bool BackdropEnabled { get; private set; }
    public bool AnimationEnabled => _animateContent;
    public event Action<double>? ShowRendered;
    public event Action<AppEntry, bool>? LaunchRequested;
    public event Action<AppEntry, string>? CategoryChangeRequested;
    public event Action<AppEntry>? CategoryResetRequested;
    public event Action<AppEntry>? PinRequested;
    public event Action<AppEntry>? HideRequested;
    public event Action<AppEntry>? LocationRequested;
    public event Action? SettingsRequested;
    public event Action? CategoryOpened;
    public event Action? Dismissed;

    public MainWindow()
    {
        InitializeComponent();
        _hoverTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(450) };
        _hoverTimer.Tick += OnHoverTimerTick;
        _animationCacheReleaseTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(180) };
        _animationCacheReleaseTimer.Tick += OnAnimationCacheReleaseTick;
        _contentFade.Freeze(); _contentSlide.Freeze(); _contentScaleX.Freeze(); _contentScaleY.Freeze();
        _viewReady = true;
        RebuildRows(); UpdateAnimationPreference();
        ApplicationThemeManager.Changed += OnApplicationThemeChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        RenderCapability.TierChanged += OnRenderingTierChanged;
    }
    public void Configure(CatalogService catalog) { _catalog = catalog; UpdateCatalog(catalog.Apps); }
    public void WarmUp()
    {
        _warming = true;
        Handle = new WindowInteropHelper(this).EnsureHandle();
        NativeWindow.ConfigureToolWindow(Handle);
        _source = HwndSource.FromHwnd(Handle);
        _source.AddHook(WindowMessage);
        ApplyAppearance();
        HotkeyRegistered = NativeWindow.RegisterShortcut(Handle, "Ctrl+Alt+Space");
        Opacity = 0; Show(); UpdateLayout(); Hide(); Opacity = 1;
        _warming = false;
    }
    public bool SetHotkey(string shortcut)
    {
        var success = NativeWindow.RegisterShortcut(Handle, shortcut);
        if (success) HotkeyRegistered = true;
        return success;
    }
    public void UpdateCatalog(IReadOnlyList<AppEntry> apps)
    {
        _catalog ??= ((App)System.Windows.Application.Current).Catalog;
        _apps = apps;
        _tiles.Clear();
        foreach (var app in apps) _tiles[app.Id] = new(app, _catalog?.Icons, _catalog?.Config.PinnedAppIds.Contains(app.Id) == true);
        RebuildRows();
        if (!IsVisible) PrepareCachedView();
    }
    public void UpdateStatus(string status) => StatusText.Text = status;
    private void PrepareCachedView()
    {
        // Realize only the viewport's recycling rows while hidden. No shell or image IO.
        if (Content is not FrameworkElement frame) return;
        var width = double.IsFinite(Width) && Width > 0 ? Width : 688;
        var height = double.IsFinite(Height) && Height > 0 ? Height : 680;
        frame.Measure(new Size(width, height));
        frame.Arrange(new Rect(0, 0, width, height));
        frame.UpdateLayout();
    }
    private void UpdateAnimationPreference()
    {
        _animateContent = SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast && (RenderCapability.Tier >> 16) > 0;
        if (!_animateContent) StopContentAnimation();
    }
    private void StartContentAnimation()
    {
        if (!_animateContent) return;
        // Keep the motion on content only, with a short scale-and-settle entrance.
        PanelContent.CacheMode = new BitmapCache { EnableClearType = true, SnapsToDevicePixels = true };
        _animationCacheReleaseTimer.Stop();
        _animationCacheReleaseTimer.Start();
        PanelContent.BeginAnimation(OpacityProperty, _contentFade, HandoffBehavior.SnapshotAndReplace);
        ContentOffset.BeginAnimation(TranslateTransform.YProperty, _contentSlide, HandoffBehavior.SnapshotAndReplace);
        ContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, _contentScaleX, HandoffBehavior.SnapshotAndReplace);
        ContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, _contentScaleY, HandoffBehavior.SnapshotAndReplace);
    }
    private void OnAnimationCacheReleaseTick(object? sender, EventArgs e)
    {
        _animationCacheReleaseTimer.Stop();
        PanelContent.CacheMode = null;
    }
    private void StopContentAnimation()
    {
        _animationCacheReleaseTimer.Stop();
        PanelContent.CacheMode = null;
        PanelContent.BeginAnimation(OpacityProperty, null);
        ContentOffset.BeginAnimation(TranslateTransform.YProperty, null);
        ContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }
    private void ApplyAppearance()
    {
        if (Handle == 0) return;
        var theme = ApplicationThemeManager.GetAppTheme();
        var highContrast = SystemParameters.HighContrast || theme == ApplicationTheme.HighContrast;
        var mica = !highContrast && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);
        WindowBackdropType = mica ? Wpf.Ui.Controls.WindowBackdropType.Mica : Wpf.Ui.Controls.WindowBackdropType.None;
        WindowBackgroundManager.UpdateBackground(this, theme, WindowBackdropType);
        BackdropEnabled = NativeWindow.ApplyAppearance(Handle, theme == ApplicationTheme.Dark, highContrast);
        if (!BackdropEnabled) SetResourceReference(BackgroundProperty, highContrast ? SystemColors.WindowBrushKey : "ApplicationBackgroundBrush");
        SetResourceReference(ForegroundProperty, highContrast ? SystemColors.WindowTextBrushKey : "TextFillColorPrimaryBrush");
    }
    private void OnApplicationThemeChanged(ApplicationTheme theme, Color accent)
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(ApplyAppearance); return; }
        ApplyAppearance();
    }
    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast) or nameof(SystemParameters.ClientAreaAnimation))
        {
            if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(() => { UpdateAnimationPreference(); ApplyAppearance(); }); return; }
            UpdateAnimationPreference(); ApplyAppearance();
        }
    }
    private void OnRenderingTierChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(UpdateAnimationPreference); return; }
        UpdateAnimationPreference();
    }
    private void RebuildRows()
    {
        if (!_viewReady) return;
        _hoverTimer.Stop();
        if (_hoverCandidate is not null) _hoverCandidate.ShowHoverActions = false;
        _hoverCandidate = null;
        ClearSelection();
        var search = SearchBox.Text.Trim();
        SearchPlaceholder.Visibility = search.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var allMatching = _apps.Where(a => _catalog?.Config.HiddenAppIds.Contains(a.Id) != true)
            .Where(a => search.Length == 0 || Matches(a.DisplayName, search) || Matches(a.Publisher, search) || Matches(a.Description, search)).ToArray();
        var matching = _activeCategory is null ? allMatching : allMatching.Where(a => a.Category.Equals(_activeCategory, StringComparison.OrdinalIgnoreCase)).ToArray();
        _displayedAppCount = matching.Length;
        var frequent = (_catalog?.FrequentApps ?? allMatching.Where(a => a.Category != Categories.Junk).Take(8).ToArray())
            .Where(a => _tiles.ContainsKey(a.Id)).Select(a => new AppTile(a, _catalog?.Icons, _tiles[a.Id].IsPinned)).ToArray();
        FrequentItems.ItemsSource = frequent;
        FrequentSection.Visibility = _activeCategory is null && search.Length == 0 && frequent.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CategoryNavigation.Visibility = _activeCategory is null ? Visibility.Collapsed : Visibility.Visible;
        CategoryTitle.Text = _activeCategory ?? string.Empty;
        _rows.Clear(); _visibleTiles.Clear(); _tileRows.Clear();
        _categoryCardCount = 0;
        var order = (_catalog?.CategoryOrder ?? Categories.Defaults).Select((name, index) => (name, index)).ToDictionary(x => x.name, x => x.index, StringComparer.OrdinalIgnoreCase);
        var groups = matching.GroupBy(a => a.Category).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var names = _activeCategory is not null
            ? new[] { _activeCategory }
            : search.Length == 0 ? groups.Keys.Concat(_catalog?.Config.Categories ?? [.. Categories.Defaults]).Distinct(StringComparer.OrdinalIgnoreCase) : groups.Keys;
        var orderedNames = names.Where(n => groups.ContainsKey(n)).OrderBy(n => n == Categories.Junk ? int.MaxValue : n == Categories.Other ? int.MaxValue - 1 : order.GetValueOrDefault(n, int.MaxValue - 2)).ToArray();
        if (_activeCategory is null)
        {
            var cards = orderedNames.Select(name =>
            {
                var group = groups[name];
                AppTile[] previews = name.Equals(Categories.Junk, StringComparison.OrdinalIgnoreCase) ? [] : group.Take(4).Select(a => _tiles[a.Id]).ToArray();
                return new CatalogCategoryCard(name, group.Length, previews);
            }).ToArray();
            _categoryCardCount = cards.Length;
            foreach (var chunk in cards.Chunk(4))
            {
                var row = new CatalogCategoryRow(chunk);
                _rows.Add(row);
                foreach (var card in row.Cards)
                foreach (var tile in card.PreviewTiles)
                {
                    _visibleTiles.Add(tile);
                    _tileRows[tile] = row;
                }
            }
        }
        else
        {
            foreach (var name in orderedNames)
            {
                var group = groups[name];
                var collapsed = name == Categories.Junk && search.Length == 0 && _collapsed.Contains(name);
                _rows.Add(new CatalogHeader(name, group.Length, collapsed));
                if (collapsed) continue;
                foreach (var chunk in group.Chunk(6))
                {
                    var row = new CatalogTileRow(chunk.Select(a => _tiles[a.Id]).ToArray());
                    _rows.Add(row);
                    foreach (var tile in row.Tiles) { _visibleTiles.Add(tile); _tileRows[tile] = row; }
                }
            }
        }
        TileList.ItemsSource = null;
        TileList.ItemsSource = _rows;
        EmptyState.Visibility = matching.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = search.Length == 0 ? "Henüz uygulama yok" : "Eşleşen uygulama yok";
        EmptyDescription.Text = search.Length == 0 ? "Kurulu uygulamalar ilk tarama tamamlanınca burada görünecek." : "Başka bir ad veya yayıncı deneyin.";
        StatusText.Text = search.Length > 0 ? $"{matching.Length} sonuç" : _activeCategory is null ? $"{_categoryCardCount} kategori · {matching.Length} uygulama" : $"{matching.Length} uygulama · Hazır";
    }
    private static bool Matches(string text, string search) => CultureInfo.CurrentCulture.CompareInfo.IndexOf(text, search, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
    private void OnSearchChanged(object sender, TextChangedEventArgs e) => RebuildRows();
    private void OnCategoryClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not CatalogHeader header) return;
        if (!_collapsed.Add(header.Name)) _collapsed.Remove(header.Name);
        RebuildRows();
    }
    public void OpenCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category)) return;
        _activeCategory = category;
        RebuildRows();
        FindChild<ScrollViewer>(TileList)?.ScrollToTop();
        CategoryOpened?.Invoke();
    }
    public void ShowAllCategories()
    {
        _activeCategory = null;
        RebuildRows();
        FindChild<ScrollViewer>(TileList)?.ScrollToTop();
    }
    internal bool RaisePreviewClickForSmoke(string appId)
    {
        if (!_tiles.TryGetValue(appId, out var tile) || !_tileRows.TryGetValue(tile, out var row)) return false;
        TileList.ScrollIntoView(row);
        TileList.UpdateLayout();
        if (TileList.ItemContainerGenerator.ContainerFromItem(row) is not DependencyObject container) return false;
        var button = FindButton(container, tile);
        button?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        return button is not null;
    }
    internal bool BeginHoverDelayForSmoke(string appId)
    {
        if (!_tiles.TryGetValue(appId, out var tile)) return false;
        _hoverCandidate = tile;
        tile.ShowHoverActions = false;
        _hoverTimer.Stop();
        _hoverTimer.Start();
        return true;
    }
    internal bool HoverActionsVisibleForSmoke(string appId) => _tiles.TryGetValue(appId, out var tile) && tile.ShowHoverActions;
    internal bool RaiseRemoveActionForSmoke(string appId)
    {
        if (!_tiles.TryGetValue(appId, out var tile) || !_tileRows.TryGetValue(tile, out var row)) return false;
        TileList.ScrollIntoView(row);
        TileList.UpdateLayout();
        if (TileList.ItemContainerGenerator.ContainerFromItem(row) is not DependencyObject container) return false;
        tile.ShowHoverActions = true;
        var button = FindActionButton(container, tile, "Uygulamayı kategoriden kaldır");
        button?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        return button is not null;
    }
    private void OnBackToCategories(object sender, RoutedEventArgs e) => ShowAllCategories();
    private void OnCategoryCardClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) is { DataContext: AppTile }) return;
        if (((FrameworkElement)sender).DataContext is CatalogCategoryCard card) OpenCategory(card.Name);
    }
    private void OnCategoryCardKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) || ((FrameworkElement)sender).DataContext is not CatalogCategoryCard card) return;
        OpenCategory(card.Name); e.Handled = true;
    }
    private void OnTileClick(object sender, RoutedEventArgs e)
    {
        if (!_suppressClick && ((FrameworkElement)sender).DataContext is AppTile tile)
        {
            LaunchRequested?.Invoke(tile.Entry, false);
            e.Handled = true;
        }
    }
    private void OnTileMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not AppTile tile) return;
        if (_hoverCandidate is not null && !ReferenceEquals(_hoverCandidate, tile)) _hoverCandidate.ShowHoverActions = false;
        _hoverCandidate = tile;
        _hoverTimer.Stop();
        tile.ShowHoverActions = false;
        _hoverTimer.Start();
    }
    private void OnTileMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not AppTile tile) return;
        if (ReferenceEquals(_hoverCandidate, tile)) { _hoverTimer.Stop(); _hoverCandidate = null; }
        tile.ShowHoverActions = false;
    }
    private void OnHoverTimerTick(object? sender, EventArgs e)
    {
        _hoverTimer.Stop();
        if (_hoverCandidate is not null) _hoverCandidate.ShowHoverActions = true;
    }
    private void OnMoveActionClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button action || action.DataContext is not AppTile tile) return;
        var root = FindAncestor<Grid>(action);
        var appButton = root is null ? null : FindCommandButton(root, tile);
        if (appButton is null) return;
        BuildTileContextMenu(appButton, tile);
        var menu = appButton.ContextMenu!;
        menu.PlacementTarget = appButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        SuppressAutoHide = true;
        menu.IsOpen = true;
    }
    private void OnRemoveActionClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: AppTile tile }) HideRequested?.Invoke(tile.Entry);
    }
    private void OnTileContextMenuClosed(object sender, RoutedEventArgs e) => SuppressAutoHide = false;
    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void OnTileContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not AppTile tile || sender is not Button button) return;
        BuildTileContextMenu(button, tile);
        SuppressAutoHide = true;
    }
    private void BuildTileContextMenu(Button button, AppTile tile)
    {
        var menu = button.ContextMenu!;
        menu.Items.Clear();
        var category = new MenuItem { Header = "Kategori değiştir" };
        foreach (var name in _catalog?.Config.Categories ?? [.. Categories.Defaults])
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = tile.Entry.Category == name };
            item.Click += (_, _) => CategoryChangeRequested?.Invoke(tile.Entry, name);
            category.Items.Add(item);
        }
        category.Items.Add(new Separator());
        var reset = new MenuItem { Header = "Otomatik kategoriyi geri yükle", IsEnabled = _catalog?.Config.CategoryOverrides.ContainsKey(tile.Entry.Id) == true };
        reset.Click += (_, _) => CategoryResetRequested?.Invoke(tile.Entry);
        category.Items.Add(reset);
        menu.Items.Add(category);
        AddMenu(menu, tile.IsPinned ? "Sabitlemeyi kaldır" : "Sabitle", () => PinRequested?.Invoke(tile.Entry));
        AddMenu(menu, "Gizle", () => HideRequested?.Invoke(tile.Entry));
        menu.Items.Add(new Separator());
        AddMenu(menu, "Dosya konumunu aç", () => LocationRequested?.Invoke(tile.Entry));
        var admin = new MenuItem { Header = "Yönetici olarak çalıştır", IsEnabled = tile.Entry.Source != AppSource.Store };
        admin.Click += (_, _) => LaunchRequested?.Invoke(tile.Entry, true);
        if (!admin.IsEnabled) { admin.ToolTip = "Store uygulamaları Windows üzerinden normal kullanıcı olarak başlatılır."; ToolTipService.SetShowOnDisabled(admin, true); }
        menu.Items.Add(admin);
    }
    private static Button? FindCommandButton(DependencyObject parent, AppTile tile)
    {
        if (parent is Button button && button.ContextMenu is not null && ReferenceEquals(button.DataContext, tile)) return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindCommandButton(VisualTreeHelper.GetChild(parent, i), tile) is { } found) return found;
        return null;
    }
    private static Button? FindActionButton(DependencyObject parent, AppTile tile, string accessibleName)
    {
        if (parent is Button button && ReferenceEquals(button.DataContext, tile) && AutomationProperties.GetName(button) == accessibleName) return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindActionButton(VisualTreeHelper.GetChild(parent, i), tile, accessibleName) is { } found) return found;
        return null;
    }
    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(current); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T match) return match;
        return null;
    }
    private void OnTileMouseDown(object sender, MouseButtonEventArgs e)
    {
        _suppressClick = false;
        _dragTile = ((FrameworkElement)sender).DataContext as AppTile;
        _dragStart = e.GetPosition(this);
    }
    private void OnTileMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _dragTile = null; return; }
        if (_dragging || _dragTile is null) return;
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var tile = _dragTile;
        _dragTile = null;
        _dragging = true; _suppressClick = true;
        ((UIElement)sender).ReleaseMouseCapture();
        try
        {
            var payload = new AppDragPayload(tile.Entry.Id, _dragSession);
            DragDrop.DoDragDrop((DependencyObject)sender, new System.Windows.DataObject(typeof(AppDragPayload), payload), System.Windows.DragDropEffects.Move);
        }
        finally
        {
            _dragging = false;
            ResetDropHighlight();
            _ = Dispatcher.BeginInvoke(() => _suppressClick = false, System.Windows.Threading.DispatcherPriority.Background);
            if (!IsActive && !SuppressAutoHide) HidePanel();
        }
        e.Handled = true;
    }
    private void OnPanelMouseUp(object sender, MouseButtonEventArgs e) => _dragTile = null;
    private bool TryGetDraggedApp(System.Windows.DragEventArgs e, out AppEntry? app)
    {
        app = null;
        if (!e.Data.GetDataPresent(typeof(AppDragPayload))) return false;
        if (e.Data.GetData(typeof(AppDragPayload)) is not AppDragPayload payload || payload.Session != _dragSession) return false;
        app = _apps.FirstOrDefault(a => a.Id.Equals(payload.AppId, StringComparison.OrdinalIgnoreCase));
        return app is not null;
    }
    private static string? DropCategory(object sender) => ((FrameworkElement)sender).DataContext switch
    {
        CatalogHeader header => header.Name,
        CatalogTileRow row => row.Category,
        CatalogCategoryCard card => card.Name,
        _ => null
    };
    private void OnCategoryDragOver(object sender, System.Windows.DragEventArgs e)
    {
        var accepted = TryGetDraggedApp(e, out _) && DropCategory(sender) is not null;
        e.Effects = accepted ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.None;
        if (accepted) HighlightDrop((FrameworkElement)sender); else ResetDropHighlight();
        e.Handled = true;
    }
    private void OnCategoryDragLeave(object sender, System.Windows.DragEventArgs e) { ResetDropHighlight(); e.Handled = true; }
    private void OnCategoryDrop(object sender, System.Windows.DragEventArgs e)
    {
        ResetDropHighlight();
        if (TryGetDraggedApp(e, out var app) && DropCategory(sender) is { } category)
        {
            CategoryChangeRequested?.Invoke(app!, category);
            e.Effects = System.Windows.DragDropEffects.Move;
        }
        else e.Effects = System.Windows.DragDropEffects.None;
        e.Handled = true;
    }
    private void OnListDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (!TryGetDraggedApp(e, out _) || (_lastDragScroll != 0 && Stopwatch.GetElapsedTime(_lastDragScroll).TotalMilliseconds < 80)) return;
        var point = e.GetPosition(TileList);
        var scroll = FindChild<ScrollViewer>(TileList);
        if (scroll is null) return;
        if (point.Y < 32) scroll.LineUp();
        else if (point.Y > TileList.ActualHeight - 32) scroll.LineDown();
        else return;
        _lastDragScroll = Stopwatch.GetTimestamp();
    }
    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindChild<T>(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }
    private void HighlightDrop(FrameworkElement element)
    {
        if (ReferenceEquals(_dropHighlight, element)) return;
        ResetDropHighlight();
        _dropHighlight = element;
        var background = new SolidColorBrush(Color.FromArgb(35, 212, 180, 111));
        var border = new SolidColorBrush(Color.FromArgb(180, 212, 180, 111));
        if (element is Control control) { _dropBackground = control.Background; _dropBorder = control.BorderBrush; control.Background = background; control.BorderBrush = border; }
        else if (element is Border surface) { _dropBackground = surface.Background; _dropBorder = surface.BorderBrush; surface.Background = background; surface.BorderBrush = border; }
        else if (element is Panel panel) { _dropBackground = panel.Background; panel.Background = background; }
    }
    private void ResetDropHighlight()
    {
        if (_dropHighlight is Control control) { control.Background = _dropBackground; control.BorderBrush = _dropBorder; }
        else if (_dropHighlight is Border border) { border.Background = _dropBackground; border.BorderBrush = _dropBorder; }
        else if (_dropHighlight is Panel panel) panel.Background = _dropBackground;
        _dropHighlight = null;
    }
    private static void AddMenu(ContextMenu menu, string text, Action action) { var item = new MenuItem { Header = text }; item.Click += (_, _) => action(); menu.Items.Add(item); }
    public void Toggle() { if (IsVisible) HidePanel(); else ShowPanel(); }
    public void ToggleFromInvocation()
    {
        if (!IsVisible && _lastDeactivated != 0 && Stopwatch.GetElapsedTime(_lastDeactivated).TotalMilliseconds < 300)
        {
            _lastDeactivated = 0;
            return;
        }
        Toggle();
    }
    public void ShowPanel()
    {
        if (IsVisible) return;
        if (SearchBox.Text.Length > 0) SearchBox.Clear();
        NativeWindow.Place(this, Handle);
        LastShowRenderMs = double.NaN;
        _rendered ??= OnShowRendering;
        CompositionTarget.Rendering -= _rendered;
        CompositionTarget.Rendering += _rendered;
        StartContentAnimation();
        _showStarted = Stopwatch.GetTimestamp();
        Show(); Activate(); NativeWindow.Activate(Handle);
        SearchBox.Focus(); Keyboard.Focus(SearchBox);
        Diagnostics.WriteTiming("SHOW_RETURN", Stopwatch.GetElapsedTime(_showStarted).TotalMilliseconds, Handle);
    }
    private void OnShowRendering(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= _rendered;
        LastShowRenderMs = Stopwatch.GetElapsedTime(_showStarted).TotalMilliseconds;
        Diagnostics.WriteTiming("SHOW_RENDER", LastShowRenderMs, Handle);
        ShowRendered?.Invoke(LastShowRenderMs);
    }
    public void HidePanel()
    {
        var wasVisible = IsVisible;
        if (_rendered is not null) CompositionTarget.Rendering -= _rendered;
        _hiding = true;
        try { Hide(); } finally { _hiding = false; }
        StopContentAnimation();
        if (SearchBox.Text.Length > 0) { SearchBox.Clear(); PrepareCachedView(); }
        ClearSelection();
        if (wasVisible) Dismissed?.Invoke();
    }
    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled) { if (message == 0x0312) { Toggle(); handled = true; } return 0; }
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { HidePanel(); e.Handled = true; return; }
        if (e.Key == Key.Enter)
        {
            if (!SearchBox.IsKeyboardFocusWithin && Keyboard.FocusedElement is Button focused && focused.DataContext is not AppTile) return;
            var tile = !SearchBox.IsKeyboardFocusWithin && Keyboard.FocusedElement is Button { DataContext: AppTile focusedTile } ? focusedTile
                : _selection >= 0 && _selection < _visibleTiles.Count ? _visibleTiles[_selection] : _visibleTiles.FirstOrDefault();
            if (tile is not null) LaunchRequested?.Invoke(tile.Entry, false);
            e.Handled = true; return;
        }
        if (SearchBox.IsKeyboardFocusWithin && e.Key is Key.Left or Key.Right) return;
        var verticalStep = _activeCategory is null ? 4 : 6;
        var offset = e.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -verticalStep, Key.Down => verticalStep, _ => 0 };
        if (offset == 0 || _visibleTiles.Count == 0) return;
        SelectTile(_selection < 0 ? 0 : Math.Clamp(_selection + offset, 0, _visibleTiles.Count - 1));
        e.Handled = true;
    }
    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (SearchBox.IsKeyboardFocusWithin || string.IsNullOrEmpty(e.Text) || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return;
        SearchBox.Focus(); SearchBox.AppendText(e.Text); SearchBox.CaretIndex = SearchBox.Text.Length; e.Handled = true;
    }
    private void ClearSelection() { if (_selection >= 0 && _selection < _visibleTiles.Count) _visibleTiles[_selection].IsSelected = false; _selection = -1; }
    private void SelectTile(int index)
    {
        ClearSelection(); _selection = index;
        var tile = _visibleTiles[index]; tile.IsSelected = true;
        if (!_tileRows.TryGetValue(tile, out var row)) return;
        TileList.ScrollIntoView(row); TileList.UpdateLayout();
        if (TileList.ItemContainerGenerator.ContainerFromItem(row) is DependencyObject container) FindButton(container, tile)?.Focus();
    }
    private static Button? FindButton(DependencyObject parent, AppTile tile)
    {
        if (parent is Button button && ReferenceEquals(button.DataContext, tile)) return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) if (FindButton(VisualTreeHelper.GetChild(parent, i), tile) is { } found) return found;
        return null;
    }
    private void OnDeactivated(object sender, EventArgs e)
    {
        if (!_warming && !_hiding && !_dragging && !SuppressAutoHide && IsVisible)
        {
            _lastDeactivated = Stopwatch.GetTimestamp();
            HidePanel();
        }
    }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { if (!System.Windows.Application.Current.Dispatcher.HasShutdownStarted) { e.Cancel = true; HidePanel(); } base.OnClosing(e); }
    public void Dispose()
    {
        NativeWindow.UnregisterShortcut(Handle);
        if (_source is not null) _source.RemoveHook(WindowMessage);
        _hoverTimer.Stop();
        _hoverTimer.Tick -= OnHoverTimerTick;
        if (_rendered is not null) CompositionTarget.Rendering -= _rendered;
        ApplicationThemeManager.Changed -= OnApplicationThemeChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        RenderCapability.TierChanged -= OnRenderingTierChanged;
    }
}
