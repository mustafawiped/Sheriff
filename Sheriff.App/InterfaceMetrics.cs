using System.Windows.Media;

namespace Sheriff.App;

/// <summary>One density setting keeps text, icons, spacing, and window sizes proportional.</summary>
public static class InterfaceMetrics
{
    public const double Scale = 0.765;
    public const double MainWidth = 704 * Scale;
    public const double MainHeight = 760 * Scale;
    public const double SettingsWidth = 520 * Scale;
    public const double SettingsHeight = 650 * Scale;
    public const double SettingsMinHeight = 420 * Scale;
    public const double ManagerWidth = 1280 * Scale;
    public const double ManagerHeight = 850 * Scale;
    public const double ManagerMinWidth = 760 * Scale;
    public const double ManagerMinHeight = 540 * Scale;
    public static Transform ContentScale { get; } = CreateScale();

    private static Transform CreateScale()
    {
        var transform = new ScaleTransform(Scale, Scale);
        transform.Freeze();
        return transform;
    }
}
