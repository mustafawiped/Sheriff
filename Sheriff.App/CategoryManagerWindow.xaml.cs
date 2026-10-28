using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Sheriff.Core;
using Sheriff.Core.Categorization;

namespace Sheriff.App;

public partial class CategoryManagerWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly SheriffConfig _draft;
    private readonly IReadOnlyList<AppEntry> _apps;
    private readonly AppCategorizer _categorizer = new();
    private readonly ShellIconCache _icons = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sheriff", "icons"));
    private readonly HashSet<string> _originalManualAssignments;
    private readonly ObservableCollection<CategoryChoice> _categories = [];
    private CategoryChoice? _selected;
    private bool _ready, _refreshing;
    public SheriffConfig Result => _draft;

    public CategoryManagerWindow(SheriffConfig config, IReadOnlyList<AppEntry> apps)
    {
        _draft = ConfigDraft.Clone(config);
        _apps = apps;
        _originalManualAssignments = new(config.CategoryOverrides.Keys, StringComparer.OrdinalIgnoreCase);
        InitializeComponent();
        SourceInitialized += (_, _) => FitToOwnerMonitor();
        if (Environment.GetEnvironmentVariable("SHERIFF_UI_TEST") == "1") ShowInTaskbar = true;
        BuildPalette();
        CategoryList.ItemsSource = _categories;
        _ready = true;
        RefreshCategories(_draft.Categories.FirstOrDefault());
        ApplyResponsiveLayout();
    }

    private void FitToOwnerMonitor()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var ownerHandle = Owner is null ? handle : new WindowInteropHelper(Owner).Handle;
        var monitor = MonitorFromWindow(ownerHandle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var dpi = GetDpiForWindow(ownerHandle);
        var scale = (dpi == 0 ? 96 : dpi) / 96d;
        var availableWidth = (info.Work.Right - info.Work.Left) / scale - 24;
        var availableHeight = (info.Work.Bottom - info.Work.Top) / scale - 24;
        MinWidth = Math.Min(InterfaceMetrics.ManagerMinWidth, Math.Max(1, availableWidth));
        MinHeight = Math.Min(InterfaceMetrics.ManagerMinHeight, Math.Max(1, availableHeight));
        Width = Math.Max(1, Math.Min(InterfaceMetrics.ManagerWidth, availableWidth));
        Height = Math.Max(1, Math.Min(InterfaceMetrics.ManagerHeight, availableHeight));
        WindowStartupLocation = WindowStartupLocation.Manual;
        var left = info.Work.Left + ((info.Work.Right - info.Work.Left) - Width * scale) / 2;
        var top = info.Work.Top + ((info.Work.Bottom - info.Work.Top) - Height * scale) / 2;
        Left = left / scale;
        Top = top / scale;
        SetWindowPos(handle, 0, (int)Math.Round(left), (int)Math.Round(top), (int)Math.Round(Width * scale), (int)Math.Round(Height * scale), 0x0014);
        ApplyResponsiveLayout();
    }

    private void WindowSizeChanged(object sender, SizeChangedEventArgs e) { if (_ready) ApplyResponsiveLayout(); }
    private void PersonalizationToggled(object sender, RoutedEventArgs e) { if (_ready) ApplyResponsiveLayout(); }
    private bool CompactLayout => Height / InterfaceMetrics.Scale < 720 || Width / InterfaceMetrics.Scale < 1050;
    private void ApplyResponsiveLayout()
    {
        var compact = CompactLayout;
        RootLayout.Margin = compact ? new Thickness(16) : new Thickness(24, 20, 24, 20);
        WindowHeading.Margin = new Thickness(0, 0, 0, compact ? 12 : 20);
        SelectedHeading.Margin = new Thickness(0, 0, 0, compact ? 6 : 10);
        WindowFooter.Margin = new Thickness(0, compact ? 12 : 16, 0, 0);
        CustomizationExpander.Margin = new Thickness(0, 0, 0, compact ? 8 : 12);
        TransferPanel.Padding = compact ? new Thickness(10, 8, 10, 8) : new Thickness(14, 12, 14, 12);
        TransferPanel.Margin = new Thickness(0, compact ? 8 : 12, 0, 0);
        SelectedCategoryTitle.FontSize = compact ? 20 : 23;
        AppsGrid.RowHeight = compact ? 64 : 68;
        AppsGrid.ColumnHeaderHeight = compact ? 32 : 36;
        AppSelectionHeading.FontSize = compact ? 16 : 18;
        SelectAllButton.Content = compact ? "Görünenleri ekle" : "Görünenleri kategoriye ekle";
        ClearSelectionButton.Content = compact ? "Görünenleri çıkar" : "Görünenleri kategoriden çıkar";
        AppCountText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SidebarColumn.Width = new GridLength(Width / InterfaceMetrics.Scale < 1050 ? 220 : 270);
        CurrentCategoryColumn.Width = new DataGridLength(Width / InterfaceMetrics.Scale < 1050 ? 140 : 180);
        WindowTitle.FontSize = compact ? 24 : 28;
        WindowIntro.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        FooterHint.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SelectedCategoryStatus.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        // At short desktop heights, personalization temporarily uses the workspace.
        var appsVisible = compact && CustomizationExpander.IsExpanded ? Visibility.Collapsed : Visibility.Visible;
        AppSelectionToolbar.Visibility = appsVisible;
        ApplicationsRegion.Visibility = appsVisible;
        TransferPanel.Visibility = appsVisible;
        UpdateSelectionState();
    }

    private static bool Required(string id) => string.Equals(id, Categories.Other, StringComparison.OrdinalIgnoreCase)
        || string.Equals(id, Categories.Junk, StringComparison.OrdinalIgnoreCase);

    private void RefreshCategories(string? selectedId = null)
    {
        var targetId = TargetCategoryBox.SelectedValue as string;
        _refreshing = true;
        try
        {
            var ids = _draft.Categories.Concat(_draft.AvailableCategories)
                .Concat(_draft.CategoryOverrides.Values).Concat(Categories.Defaults)
                .Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            _categories.Clear();
            foreach (var id in ids)
                _categories.Add(new CategoryChoice(id, CategoryCustomization.GetDisplayName(_draft, id),
                    _draft.Categories.Contains(id, StringComparer.OrdinalIgnoreCase), Required(id),
                    CategoryCustomization.GetColor(_draft, id), CategoryEnabledChanged));
            _draft.AvailableCategories = ids.ToList();
            RefreshCounts();
            TargetCategoryBox.ItemsSource = _categories.Where(c => c.Enabled).ToArray();
            CategoryList.SelectedItem = _categories.FirstOrDefault(c => string.Equals(c.Id, selectedId, StringComparison.OrdinalIgnoreCase)) ?? _categories.FirstOrDefault();
            _selected = CategoryList.SelectedItem as CategoryChoice;
            TargetCategoryBox.SelectedValue = _categories.Any(c => c.Enabled && string.Equals(c.Id, targetId, StringComparison.OrdinalIgnoreCase)) ? targetId : _selected?.Enabled == true ? _selected.Id : _draft.Categories.FirstOrDefault();
        }
        finally { _refreshing = false; }
        UpdateSelectedCategory();
        RefreshApps();
    }

    private void RefreshCounts()
    {
        var counts = _apps.GroupBy(EffectiveCategory, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var category in _categories)
            category.Count = counts.GetValueOrDefault(category.Id);
    }

    private string EffectiveCategory(AppEntry app)
    {
        // Show the original manual assignment even while its category is disabled.
        if (_draft.CategoryOverrides.TryGetValue(app.Id, out var assigned)) return assigned;
        var classified = _categorizer.Classify(app, _draft);
        if (classified.IsUnclassified && !_originalManualAssignments.Contains(app.Id)
            && _draft.Categories.Contains(app.Category, StringComparer.OrdinalIgnoreCase)) return app.Category;
        return classified.Category;
    }

    private void CategoryEnabledChanged(CategoryChoice choice)
    {
        if (!_ready || _refreshing) return;
        if (choice.Enabled)
        {
            if (!_draft.Categories.Contains(choice.Id, StringComparer.OrdinalIgnoreCase))
            {
                if (_draft.Categories.Count >= 30)
                {
                    choice.RestoreEnabled(false);
                    FeedbackText.Text = "En fazla 30 kategori etkin olabilir.";
                    return;
                }
                _draft.Categories.Add(choice.Id);
            }
        }
        else if (!Required(choice.Id)) _draft.Categories.RemoveAll(c => string.Equals(c, choice.Id, StringComparison.OrdinalIgnoreCase));
        RefreshCounts();
        var targetId = TargetCategoryBox.SelectedValue as string;
        TargetCategoryBox.ItemsSource = _categories.Where(c => c.Enabled).ToArray();
        TargetCategoryBox.SelectedValue = _categories.Any(c => c.Enabled && c.Id == targetId) ? targetId : _draft.Categories.FirstOrDefault();
        UpdateSelectedCategory();
        RefreshApps();
        FeedbackText.Text = choice.Enabled ? $"{choice.Name} ana sayfada gösterilecek." : $"{choice.Name} kapatıldı. Uygulamalar ve elle yapılan atamalar saklanıyor.";
    }

    private void CategorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _refreshing) return;
        _selected = CategoryList.SelectedItem as CategoryChoice;
        if (_selected?.Enabled == true) TargetCategoryBox.SelectedValue = _selected.Id;
        UpdateSelectedCategory();
        RefreshApps();
    }

    private void UpdateSelectedCategory()
    {
        SelectedCategoryTitle.Text = _selected?.Name ?? "Kategori seçin";
        CategoryNameBox.Text = _selected?.Name ?? "";
        ColorBox.Text = _selected is null ? "" : CategoryCustomization.GetColor(_draft, _selected.Id) ?? "";
        SelectedCategoryStatus.Text = _selected is null ? "" : $"{_selected.Count} uygulama · {(_selected.Enabled ? "Ana sayfada gösteriliyor" : "Ana sayfada kapalı")}";
    }

    private void AddCategoryClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var id = CategoryCustomization.AddCategory(_draft, NewCategoryBox.Text);
            NewCategoryBox.Clear();
            RefreshCategories(id);
            TargetCategoryBox.SelectedValue = id;
            ScopeBox.SelectedIndex = 1;
            FeedbackText.Text = $"{CategoryCustomization.GetDisplayName(_draft, id)} eklendi. Uygulamaları listeden seçip taşıyabilirsiniz.";
        }
        catch (ArgumentException ex) { FeedbackText.Text = ex.Message; }
    }

    private void RenameCategoryClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        try
        {
            var id = _selected.Id;
            CategoryCustomization.RenameCategory(_draft, id, CategoryNameBox.Text);
            RefreshCategories(id);
            FeedbackText.Text = "Kategori adı güncellendi. Uygulama atamaları korundu.";
        }
        catch (ArgumentException ex) { FeedbackText.Text = ex.Message; }
    }

    private void BuildPalette()
    {
        var neutral = new Button { Content = "Nötr", Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 7, 0), ToolTip = "Varsayılan nötr görünüm" };
        neutral.Click += (_, _) => SetColor(null);
        PalettePanel.Children.Add(neutral);
        foreach (var hex in new[] { "#5B9FE6", "#7C82E8", "#BD80D5", "#D780A1", "#DBA465", "#63B59C" })
        {
            var brush = (Brush)new BrushConverter().ConvertFromString(hex)!;
            brush.Freeze();
            var button = new Button
            {
                Width = 23, Height = 23, Margin = new Thickness(0, 0, 5, 0), Padding = new Thickness(0),
                Background = brush, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), ToolTip = hex
            };
            System.Windows.Automation.AutomationProperties.SetName(button, "Kategori rengi " + hex);
            button.Click += (_, _) => SetColor(hex);
            PalettePanel.Children.Add(button);
        }
    }

    private void ApplyColorClick(object sender, RoutedEventArgs e) => SetColor(ColorBox.Text);
    private void SetColor(string? color)
    {
        if (_selected is null) return;
        try
        {
            var id = _selected.Id;
            CategoryCustomization.SetColor(_draft, id, color);
            RefreshCategories(id);
            FeedbackText.Text = string.IsNullOrWhiteSpace(color) ? "Nötr tema seçildi." : "Kategori rengi güncellendi.";
        }
        catch (ArgumentException ex) { FeedbackText.Text = ex.Message; }
    }

    private void AppSearchChanged(object sender, TextChangedEventArgs e) { if (_ready) RefreshApps(); }
    private void ScopeChanged(object sender, SelectionChangedEventArgs e) { if (_ready) RefreshApps(); }

    private void RefreshApps()
    {
        if (!_ready) return;
        var query = AppSearchBox.Text.Trim();
        var all = ScopeBox.SelectedIndex == 1;
        var rows = _apps.Where(app => !AutoBlockPolicy.IsBlocked(app, _draft)).Select(app => new { App = app, Category = EffectiveCategory(app) })
            .Where(item => all || _selected is not null && string.Equals(item.Category, _selected.Id, StringComparison.OrdinalIgnoreCase))
            .Where(item => query.Length == 0 || item.App.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || item.App.Publisher.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(item => item.App.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => new AppChoice(item.App.Id, item.App.DisplayName, item.App.Publisher,
                CategoryCustomization.GetDisplayName(_draft, item.Category), item.Category,
                _draft.HiddenAppIds.Contains(item.App.Id) ? "Gizli" : _draft.CategoryOverrides.ContainsKey(item.App.Id) ? "Elle seçildi" : "Otomatik kategori",
                item.App.IconCachePath, _icons, _selected is not null && string.Equals(item.Category, _selected.Id, StringComparison.OrdinalIgnoreCase), _selected?.Enabled == true, included =>
                {
                    if (_selected is null) return;
                    CategoryCustomization.SetMembership(_draft, item.App.Id, _selected.Id, included, item.Category);
                    RefreshCounts();
                    UpdateSelectedCategory();
                    RefreshApps();
                    FeedbackText.Text = included ? $"{item.App.DisplayName} bu kategoriye eklendi." : $"{item.App.DisplayName} bu kategoriden çıkarıldı.";
                }))
            .ToArray();
        AppsGrid.ItemsSource = rows;
        AppCountText.Text = $"{rows.Length} / {_apps.Count} uygulama";
        UpdateAppSelectionHeading();
        SelectAllButton.IsEnabled = rows.Length > 0;
        ClearSelectionButton.IsEnabled = rows.Length > 0 && _selected?.Enabled == true;
        EmptyAppsText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionState();
    }

    private void AppSelectionChanged(object sender, SelectionChangedEventArgs e) { if (_ready) UpdateSelectionState(); }
    private void TargetCategoryChanged(object sender, SelectionChangedEventArgs e) { if (_ready) UpdateSelectionState(); }
    private void SelectAllAppsClick(object sender, RoutedEventArgs e) => SetVisibleMembership(true);
    private void ClearAppSelectionClick(object sender, RoutedEventArgs e) => SetVisibleMembership(false);
    private void SetVisibleMembership(bool included)
    {
        if (_selected?.Enabled != true) { FeedbackText.Text = "Önce etkin bir kategori seçin."; return; }
        var rows = AppsGrid.ItemsSource?.Cast<AppChoice>().ToArray() ?? [];
        foreach (var row in rows) CategoryCustomization.SetMembership(_draft, row.Id, _selected.Id, included, row.CurrentCategoryId);
        RefreshCounts();
        UpdateSelectedCategory();
        RefreshApps();
        FeedbackText.Text = included ? $"{rows.Length} görünen uygulama kategoriye eklendi." : $"{rows.Length} görünen uygulama kategoriden çıkarıldı.";
    }
    private void AppRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        var fromCheckBox = false;
        while (element is not null && element is not DataGridRow)
        {
            if (element is CheckBox) fromCheckBox = true;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        if (fromCheckBox) return;
        if (element is not DataGridRow row) return;
        AppsGrid.Focus();
        row.IsSelected = !row.IsSelected;
        // Handle the same event for checkbox and row clicks so neither toggles twice.
        e.Handled = true;
    }
    private void UpdateSelectionState()
    {
        UpdateAppSelectionHeading();
        var count = AppsGrid.SelectedItems.Count;
        AppSelectionText.Text = count == 0 ? "Seçim yapılmadı" : $"{count} uygulama seçildi";
        MoveAppsButton.Content = Width / InterfaceMetrics.Scale < 1050 ? $"3. Taşı ({count})" : count == 0 ? "3. Seçilenleri taşı" : $"3. {count} uygulamayı taşı";
        ResetAppsButton.Content = Width / InterfaceMetrics.Scale < 1050 ? "Otomatiğe dön" : "Otomatik kategori";
        ClearSelectionButton.IsEnabled = AppsGrid.Items.Count > 0 && _selected?.Enabled == true;
        MoveAppsButton.IsEnabled = count > 0 && TargetCategoryBox.SelectedValue is string;
        ResetAppsButton.IsEnabled = count > 0;
    }

    private void UpdateAppSelectionHeading() => AppSelectionHeading.Text = CompactLayout
        ? $"1. Uygulamaları seçin ({AppsGrid.Items.Count})" : "1. Uygulamaları seçin";

    private void MoveAppsClick(object sender, RoutedEventArgs e)
    {
        if (TargetCategoryBox.SelectedValue is not string target) { FeedbackText.Text = "Bir hedef kategori seçin."; return; }
        var selected = AppsGrid.SelectedItems.Cast<AppChoice>().ToArray();
        try
        {
            foreach (var app in selected) CategoryCustomization.AssignCategory(_draft, app.Id, target);
            RefreshCounts();
            UpdateSelectedCategory();
            RefreshApps();
            FeedbackText.Text = $"{selected.Length} uygulama {CategoryCustomization.GetDisplayName(_draft, target)} kategorisine taşındı.";
        }
        catch (ArgumentException ex) { FeedbackText.Text = ex.Message; }
    }

    private void ResetAppsClick(object sender, RoutedEventArgs e)
    {
        var selected = AppsGrid.SelectedItems.Cast<AppChoice>().ToArray();
        foreach (var app in selected) _draft.CategoryOverrides.Remove(app.Id);
        RefreshCounts();
        UpdateSelectedCategory();
        RefreshApps();
        FeedbackText.Text = $"{selected.Length} uygulama için otomatik sınıflandırma geri açıldı.";
    }

    private void AcceptClick(object sender, RoutedEventArgs e)
    {
        if (_selected is not null)
        {
            try
            {
                CategoryCustomization.RenameCategory(_draft, _selected.Id, CategoryNameBox.Text);
                CategoryCustomization.SetColor(_draft, _selected.Id, ColorBox.Text);
            }
            catch (ArgumentException ex) { FeedbackText.Text = ex.Message; return; }
        }
        DialogResult = true;
    }

    private void NewCategoryKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { AddCategoryClick(sender, e); e.Handled = true; } }
    private void RenameKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { RenameCategoryClick(sender, e); e.Handled = true; } }
    private void ColorKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { ApplyColorClick(sender, e); e.Handled = true; } }

    private sealed class AppChoice : INotifyPropertyChanged
    {
        private readonly Action<bool> _membershipChanged;
        private bool _isMember;
        public AppChoice(string id, string name, string publisher, string categoryName, string currentCategoryId, string assignment, string iconPath, ShellIconCache cache, bool isMember, bool canEditMembership, Action<bool> membershipChanged)
        { Id = id; Name = name; Publisher = publisher; CategoryName = categoryName; CurrentCategoryId = currentCategoryId; Assignment = assignment; IconPath = iconPath; Cache = cache; _isMember = isMember; CanEditMembership = canEditMembership; _membershipChanged = membershipChanged; }
        public string Id { get; }
        public string Name { get; }
        public string Publisher { get; }
        public string CategoryName { get; }
        public string CurrentCategoryId { get; }
        public bool CanEditMembership { get; }
        public string Assignment { get; }
        public string IconPath { get; }
        public ShellIconCache Cache { get; }
        public bool IsMember { get => _isMember; set { if (_isMember == value) return; _isMember = value; PropertyChanged?.Invoke(this, new(nameof(IsMember))); _membershipChanged(value); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        public string SelectionLabel => Name + " uygulamasını seç";
        public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[..1].ToUpperInvariant();
        public string Details => string.IsNullOrWhiteSpace(Publisher) ? Assignment : Publisher + " · " + Assignment;
        public ImageSource? Icon => Cache.GetImage(IconPath);
    }

    [StructLayout(LayoutKind.Sequential)] private struct MonitorRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public MonitorRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    private sealed class CategoryChoice(string id, string name, bool enabled, bool required, string? color, Action<CategoryChoice> changed) : INotifyPropertyChanged
    {
        private bool _enabled = enabled;
        private int _count;
        public string Id { get; } = id;
        public string Name { get; } = name;
        public bool CanToggle => !required;
        public string ToggleName => Name + " kategorisini göster";
        public string ToggleHint => required ? "Bu kategori uygulamaların düzenli biçimde gösterilmesi için gereklidir." : "Ana sayfada bu kategoriyi göster";
        public Brush Brush { get; } = CreateBrush(color);
        public bool Enabled { get => _enabled; set { if (_enabled == value) return; _enabled = value; PropertyChanged?.Invoke(this, new(nameof(Enabled))); changed(this); } }
        public int Count { get => _count; set { _count = value; PropertyChanged?.Invoke(this, new(nameof(CountLabel))); } }
        public string CountLabel => $"{_count} uygulama";
        public void RestoreEnabled(bool value) { _enabled = value; PropertyChanged?.Invoke(this, new(nameof(Enabled))); }
        public event PropertyChangedEventHandler? PropertyChanged;
        private static Brush CreateBrush(string? color)
        {
            var brush = (Brush)new BrushConverter().ConvertFromString(color ?? "#808080")!;
            brush.Freeze();
            return brush;
        }
    }
}
