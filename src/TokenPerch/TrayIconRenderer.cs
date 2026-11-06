using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace TokenPerch;

/// <summary>Draws the tray icon: the app icon (dimmed when Claude Code isn't running), the session
/// usage badge bottom-right (coloured by the highest limit) and the needs-input count top-left.</summary>
public static class TrayIconRenderer
{
    const int Size = 64;
    static readonly Bitmap Base = new(AssetLoader.Open(new Uri("avares://tokenperch/Assets/tokenperch.png")));
    static readonly Typeface Bold = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);

    public static WindowIcon Render(bool running, double? sessionPercent, double maxPercent, int needs)
    {
        using var rtb = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
        {
            using (ctx.PushOpacity(running ? 1.0 : 0.45))
                ctx.DrawImage(Base, new Rect(0, 0, Size, Size));
            if (sessionPercent is { } p)
                Badge(ctx, $"{p:0}", Palette.LevelColor(maxPercent), right: true, height: 30);
            if (needs > 0)
                Badge(ctx, needs.ToString(CultureInfo.InvariantCulture), Color.Parse("#F5A524"), right: false, height: 28);
        }
        using var ms = new MemoryStream();
        rtb.Save(ms, PngBitmapEncoderOptions.Default);
        ms.Position = 0;
        return new WindowIcon(ms);
    }

    static void Badge(DrawingContext ctx, string text, Color color, bool right, double height)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Bold, height * 0.72, Brushes.White);
        double w = Math.Max(height, ft.Width + 8);
        var rect = right
            ? new Rect(Size - w, Size - height, w, height)
            : new Rect(0, 0, w, height);
        ctx.DrawRectangle(new SolidColorBrush(color), null, rect, height / 2, height / 2);
        ctx.DrawText(ft, new Point(rect.X + (w - ft.Width) / 2, rect.Y + (height - ft.Height) / 2));
    }
}
