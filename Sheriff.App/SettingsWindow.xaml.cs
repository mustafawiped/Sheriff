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
        foreach (var app in apps.Where(a => config.HiddenAppIds.Contains(a.Id)))
        {
            var line = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var button = new Button { Content = "Geri al", Margin = new Thickness(12, 0, 0, 0), Tag = app.Id };
            DockPanel.SetDock(button, Dock.Right); line.Children.Add(button);
            line.Children.Add(new TextBlock { Text = app.DisplayName, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            button.Click += (_, _) => { _config.HiddenAppIds.Remove(app.Id); line.Visibility = Visibility.Collapsed; };
            HiddenPanel.Children.Add(line);
        }
        if (HiddenPanel.Children.Count == 0) HiddenPanel.Children.Add(new TextBlock { Text = "Kaldırılan uygulama yok.", Opacity = 0.6, FontSize = 12 });
    }
    private void UpdateCategorySummary() => CategorySummary.Text = $"{_config.Categories.Count} etkin kategori · {_apps.Count} uygulama";

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
