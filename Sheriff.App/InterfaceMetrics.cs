using System.Windows.Media;

namespace Sheriff.App;

/// <summary>One density setting keeps text, icons, spacing, and window sizes proportional.</summary>
public static class InterfaceMetrics
{
    public const double Scale = 0.8415;
    public const double MainWidth = 704 * Scale;
    public const double MainHeight = 760 * Scale;
    public const double SettingsWidth = 520 * Scale;
    public const double SettingsHeight = 650 * Scale;
    public const double SettingsMinHeight = 420 * Scale;
    public static Transform SettingsContentScale { get; } = CreateSettingsScale();
    public const double SettingsWidthEnlarged = SettingsWidth * 1.10;
    public const double SettingsHeightEnlarged = SettingsHeight * 1.10;
    public const double SettingsMinHeightEnlarged = SettingsMinHeight * 1.10;
    public const double CategoryWindowWidth = 760 * Scale;
    public const double CategoryWindowHeight = 700 * Scale;
    public const double CategoryWindowMinWidth = 520 * Scale;
    public const double CategoryWindowMinHeight = 460 * Scale;
    public const double ManagerWidth = 1280 * Scale;
    public const double ManagerHeight = 850 * Scale;
    public const double ManagerMinWidth = 760 * Scale;
    public const double ManagerMinHeight = 540 * Scale;
    public static Transform ContentScale { get; } = CreateScale();

    private static Transform CreateSettingsScale()
    {
        var transform = new ScaleTransform(Scale * 1.10, Scale * 1.10);
        transform.Freeze();
        return transform;
    }

    private static Transform CreateScale()
    {
        var transform = new ScaleTransform(Scale, Scale);
        transform.Freeze();
        return transform;
    }
}
