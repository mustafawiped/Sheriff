using System.Windows;
using System.Windows.Controls;
using Sheriff.Core;

namespace Sheriff.App;

public partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly SheriffConfig _config;
    public SheriffConfig Result => _config;
    public SettingsWindow(SheriffConfig config, IReadOnlyList<AppEntry> apps)
    {
        InitializeComponent();
        if (Environment.GetEnvironmentVariable("SHERIFF_UI_TEST") == "1") ShowInTaskbar = true;
        _config = System.Text.Json.JsonSerializer.Deserialize<SheriffConfig>(System.Text.Json.JsonSerializer.Serialize(config))!;
        HotkeyBox.Text = config.Hotkey; StartupBox.IsChecked = config.StartWithWindows;
        CategoriesBox.Text = string.Join(Environment.NewLine, config.Categories);
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
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        var categories = CategoriesBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(c => c.Length <= 40).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (categories.Count == 0 || categories.Count > 30) { ErrorText.Text = "1–30 kategori girin; kategori adları en fazla 40 karakter olabilir."; return; }
        categories.RemoveAll(c => string.Equals(c, Categories.Other, StringComparison.OrdinalIgnoreCase) || string.Equals(c, Categories.Junk, StringComparison.OrdinalIgnoreCase));
        categories.Add(Categories.Other); categories.Add(Categories.Junk);
        if (LlmBox.IsChecked == true && (!Uri.TryCreate(EndpointBox.Text, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https")) { ErrorText.Text = "LLM için geçerli bir HTTPS API adresi girin."; return; }
        _config.Categories = categories; _config.Hotkey = HotkeyBox.Text.Trim(); _config.StartWithWindows = StartupBox.IsChecked == true;
        if (LlmBox.IsChecked == true && (string.IsNullOrWhiteSpace(ApiKeyBox.Password) || string.IsNullOrWhiteSpace(ModelBox.Text))) { ErrorText.Text = "LLM için API anahtarı ve model girin."; return; }
        _config.LlmEnabled = LlmBox.IsChecked == true; _config.LlmEndpoint = "https://api.openai.com/v1/chat/completions"; _config.LlmModel = ModelBox.Text.Trim(); _config.LlmApiKey = ApiKeyBox.Password.Trim();
        DialogResult = true;
    }
}
