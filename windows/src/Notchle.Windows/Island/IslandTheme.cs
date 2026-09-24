using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Notchle.Core.Ui;

[assembly: InternalsVisibleTo("Notchle.Windows.Tests")]

namespace Notchle.Windows.Island;

/// Colours and type of the island (same palette as the macOS notch, Segoe UI instead of SF).
internal static class IslandTheme
{
    public static readonly FontFamily Font = new("Segoe UI");

    public static readonly Color Green = Color.FromRgb(0x1F, 0xD6, 0x61);
    public static readonly Color Red = Color.FromRgb(0xFF, 0x5C, 0x5C);
    public static readonly Color Yellow = Color.FromRgb(0xFF, 0xCC, 0x00);

    public static readonly Brush Black = Frozen(Colors.Black);
    public static readonly Brush GreenBrush = Frozen(Green);
    public static readonly Brush RedBrush = Frozen(Red);
    public static readonly Brush YellowBrush = Frozen(Yellow);
    public static readonly Brush Primary = Frozen(Colors.White);
    public static readonly Brush Secondary = Frozen(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));   // 55%
    public static readonly Brush Tertiary = Frozen(Color.FromArgb(0x52, 0xFF, 0xFF, 0xFF));    // 32%
    public static readonly Brush FieldFill = Frozen(Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF));   // 9%
    public static readonly Brush FieldStroke = Frozen(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)); // 14%
    public static readonly Brush SecondaryFill = Frozen(Color.FromArgb(0x21, 0xFF, 0xFF, 0xFF)); // 13%
    public static readonly Brush FaintFill = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));   // 7%
    public static readonly Brush TrackFill = Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));   // 10%
    public static readonly Brush Transparent = Frozen(Colors.Transparent);

    public static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static Brush Frozen(Color c, double opacity) => Frozen(Color.FromArgb((byte)Math.Round(255 * opacity), c.R, c.G, c.B));

    public static Color FromArgb(uint argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    /// The Windows accent colour (UISettings, then the DWM glass colour), else Spotify green.
    public static Color ReadAccent()
    {
        uint? fromUiSettings = null;
        uint? fromGlass = null;
        try
        {
            var c = new global::Windows.UI.ViewManagement.UISettings()
                .GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Accent);
            fromUiSettings = (uint)c.A << 24 | (uint)c.R << 16 | (uint)c.G << 8 | c.B;
        }
        catch (Exception) { /* WinRT unavailable (Server Core, old build): fall through. */ }
        try
        {
            var g = SystemParameters.WindowGlassColor;
            fromGlass = (uint)g.A << 24 | (uint)g.R << 16 | (uint)g.G << 8 | g.B;
        }
        catch (Exception) { }
        return FromArgb(IslandAccent.Pick(fromUiSettings, fromGlass));
    }

    /// "Show animations in Windows" (Settings > Accessibility > Visual effects).
    public static bool ReduceMotion
    {
        get
        {
            try { return !SystemParameters.ClientAreaAnimation; }
            catch (Exception) { return false; }
        }
    }
}
