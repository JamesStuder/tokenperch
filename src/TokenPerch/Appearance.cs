using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using TokenPerch.Core;

namespace TokenPerch;

/// <summary>Theme (system / light / dark) and the custom panel colours, applied by overriding the Fluent
/// theme's own brushes in the panel window's resources (removing an override restores the theme's).</summary>
public static class Appearance
{
    static readonly string[] ButtonKeys = ["ButtonBackground", "ToggleButtonBackground"];
    static readonly string[] ButtonHoverKeys = ["ButtonBackgroundPointerOver", "ToggleButtonBackgroundPointerOver"];
    static readonly string[] ButtonPressedKeys = ["ButtonBackgroundPressed", "ToggleButtonBackgroundPressed"];
    static readonly string[] ButtonTextKeys =
    [
        "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed",
        "ToggleButtonForeground", "ToggleButtonForegroundPointerOver", "ToggleButtonForegroundPressed",
    ];

    public static void ApplyTheme(AppConfig cfg)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = cfg.Theme switch
            {
                "light" => ThemeVariant.Light,
                "dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
    }

    public static void ApplyColors(AppConfig cfg, Window window)
    {
        var c = cfg.Colors;
        Set(window, ["SystemControlBackgroundAltHighBrush"], Parse(c.Background));
        Set(window, ["SystemControlForegroundBaseHighBrush"], Parse(c.Text));
        var button = Parse(c.Button);
        Set(window, ButtonKeys, button);
        Set(window, ButtonHoverKeys, button is { } b ? Shade(b, 0.12) : null);
        Set(window, ButtonPressedKeys, button is { } p ? Shade(p, -0.12) : null);
        Set(window, ButtonTextKeys, Parse(c.ButtonText));
    }

    public static Color? Parse(string? hex) => Color.TryParse(hex, out var c) ? c : null;

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    static void Set(Window window, string[] keys, Color? color)
    {
        foreach (var key in keys)
        {
            if (color is { } c) window.Resources[key] = new SolidColorBrush(c);
            else window.Resources.Remove(key);
        }
    }

    /// <summary>Lighter (amount &gt; 0) or darker, for hover / pressed.</summary>
    static Color Shade(Color c, double amount)
    {
        byte Mix(byte v) => (byte)Math.Clamp(amount > 0 ? v + (255 - v) * amount : v * (1 + amount), 0, 255);
        return Color.FromArgb(c.A, Mix(c.R), Mix(c.G), Mix(c.B));
    }
}
