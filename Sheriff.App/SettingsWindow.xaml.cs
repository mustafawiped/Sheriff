using System.Windows;
using System.Windows.Controls;
using Sheriff.Core;

namespace Sheriff.App;

public partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly SheriffConfig _config;
    private readonly IReadOnlyList<AppEntry> _apps;
    public SheriffConfig Result => _config;
    public SettingsWindow(SheriffConfig config, IReadOnlyList<AppEntry> apps)
    {
        InitializeComponent();
        if (Environment.GetEnvironmentVariable("SHERIFF_UI_TEST") == "1") ShowInTaskbar = true;
        _config = ConfigDraft.Clone(config);
        _apps = apps;
        HotkeyBox.Text = config.Hotkey; StartupBox.IsChecked = config.StartWithWindows;
        UpdateCategorySummary();
        LlmBox.IsChecked = config.LlmEnabled; EndpointBox.Text = config.LlmEndpoint; ModelBox.Text = config.LlmModel; ApiKeyBox.Password = config.LlmApiKey;
        EndpointBox.Text = "https://api.openai.com/v1/chat/completions";
        EndpointBox.IsReadOnly = true;
        foreach (var app in apps.Where(a => config.HiddenAppIds.Contains(a.Id) && AutoBlockPolicy.GetReason(a) is null))
        {
            var line = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var button = new Button { Content = "Geri al", Margin = new Thickness(12, 0, 0, 0), Tag = app.Id };
            DockPanel.SetDock(button, Dock.Right); line.Children.Add(button);
            line.Children.Add(new TextBlock { Text = app.DisplayName, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            button.Click += (_, _) => { _config.HiddenAppIds.Remove(app.Id); line.Visibility = Visibility.Collapsed; };
            HiddenPanel.Children.Add(line);
        }
        foreach (var app in apps.Where(a => AutoBlockPolicy.GetReason(a) is not null).OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var line = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            var check = new CheckBox { Content = app.DisplayName, IsChecked = _config.AllowedAutoBlockedAppIds.Contains(app.Id), Tag = app.Id };
            System.Windows.Automation.AutomationProperties.SetName(check, app.DisplayName + " otomatik engelini kaldır");
            line.Children.Add(check);
            line.Children.Add(new TextBlock { Text = AutoBlockPolicy.GetReason(app), Margin = new Thickness(22, 2, 0, 0), FontSize = 11, Opacity = 0.6 });
            check.Checked += (_, _) => SetAutoBlockedEnabled(app, true);
            check.Unchecked += (_, _) => SetAutoBlockedEnabled(app, false);
            AutoBlockedPanel.Children.Add(line);
        }
        if (AutoBlockedPanel.Children.Count == 0) AutoBlockedPanel.Children.Add(new TextBlock { Text = "Otomatik engellenen uygulama yok.", Opacity = 0.6, FontSize = 12 });
        if (HiddenPanel.Children.Count == 0) HiddenPanel.Children.Add(new TextBlock { Text = "Kaldırılan uygulama yok.", Opacity = 0.6, FontSize = 12 });
    }
    private void UpdateCategorySummary() => CategorySummary.Text = $"{_config.Categories.Count} etkin kategori · {_apps.Count} uygulama";

    private void SetAutoBlockedEnabled(AppEntry app, bool enabled)
    {
        if (enabled) { _config.AllowedAutoBlockedAppIds.Add(app.Id); _config.HiddenAppIds.Remove(app.Id); }
        else _config.AllowedAutoBlockedAppIds.Remove(app.Id);
    }

    private void EditCategoriesClick(object sender, RoutedEventArgs e)
    {
        var editor = new CategoryManagerWindow(_config, _apps) { Owner = this };
        if (editor.ShowDialog() != true) return;
        var edited = editor.Result;
        _config.Categories = edited.Categories;
        _config.AvailableCategories = edited.AvailableCategories;
        _config.CategoryOverrides = edited.CategoryOverrides;
        _config.CategoryLabels = edited.CategoryLabels;
        _config.CategoryColors = edited.CategoryColors;
        _config.HiddenAppIds = edited.HiddenAppIds;
        UpdateCategorySummary();
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (LlmBox.IsChecked == true && (!Uri.TryCreate(EndpointBox.Text, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https")) { ErrorText.Text = "LLM için geçerli bir HTTPS API adresi girin."; return; }
        _config.Hotkey = HotkeyBox.Text.Trim(); _config.StartWithWindows = StartupBox.IsChecked == true;
        if (LlmBox.IsChecked == true && (string.IsNullOrWhiteSpace(ApiKeyBox.Password) || string.IsNullOrWhiteSpace(ModelBox.Text))) { ErrorText.Text = "LLM için API anahtarı ve model girin."; return; }
        _config.LlmEnabled = LlmBox.IsChecked == true; _config.LlmEndpoint = "https://api.openai.com/v1/chat/completions"; _config.LlmModel = ModelBox.Text.Trim(); _config.LlmApiKey = ApiKeyBox.Password.Trim();
        DialogResult = true;
    }
}
