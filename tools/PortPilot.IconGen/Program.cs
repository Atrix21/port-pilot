// Renders the PortPilot mark into a multi-resolution .ico and preview PNGs.
// Usage: dotnet run --project tools/PortPilot.IconGen -- <output-directory>
//        dotnet run --project tools/PortPilot.IconGen -- --social <card.png> <screenshot.png>

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--social", var card, var screenshot])
        {
            File.WriteAllBytes(card, EncodePng(RenderSocialCard(screenshot)));
            Console.WriteLine($"Wrote social card to {card}");
            return 0;
        }

        var output = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        Directory.CreateDirectory(output);

        var frames = IconSizes.Select(size => (size, png: EncodePng(Render(size)))).ToList();
        WriteIco(Path.Combine(output, "PortPilot.ico"), frames);
        File.WriteAllBytes(Path.Combine(output, "PortPilot-512.png"), EncodePng(Render(512)));
        File.WriteAllBytes(Path.Combine(output, "PortPilot-1024.png"), EncodePng(Render(1024)));

        Console.WriteLine($"Wrote icon assets to {output}");
        return 0;
    }

    /// <summary>
    /// The mark: a charcoal squircle holding an amber "porthole" ring whose opening carries a small
    /// signal dot – an endpoint being watched.
    /// </summary>
    private static BitmapSource Render(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var s = size / 256.0;
            var small = size <= 24;

            // Squircle tile. Small sizes use the full canvas for legibility.
            var inset = small ? 0.5 : 16 * s;
            var tile = new Rect(inset, inset, size - inset * 2, size - inset * 2);
            var radius = tile.Width * (small ? 0.24 : 0.235);

            var background = new LinearGradientBrush(
                Color.FromRgb(0x33, 0x32, 0x37),
                Color.FromRgb(0x16, 0x16, 0x19),
                new Point(0.5, 0),
                new Point(0.5, 1));

            if (!small)
            {
                // Soft contact shadow so the tile sits on light and dark taskbars alike.
                var shadow = new RadialGradientBrush(Color.FromArgb(70, 0, 0, 0), Color.FromArgb(0, 0, 0, 0))
                {
                    Center = new Point(0.5, 0.56),
                    GradientOrigin = new Point(0.5, 0.56),
                    RadiusX = 0.52,
                    RadiusY = 0.52,
                };
                dc.DrawRectangle(shadow, null, new Rect(0, 4 * s, size, size));
            }

            dc.DrawRoundedRectangle(background, null, tile, radius, radius);

            if (!small)
            {
                // Hairline top highlight.
                var highlight = new LinearGradientBrush(
                    Color.FromArgb(46, 255, 255, 255),
                    Color.FromArgb(0, 255, 255, 255),
                    new Point(0.5, 0),
                    new Point(0.5, 0.5));
                var rim = new Pen(highlight, Math.Max(1, 2 * s));
                var rimRect = tile;
                rimRect.Inflate(-rim.Thickness / 2, -rim.Thickness / 2);
                dc.DrawRoundedRectangle(null, rim, rimRect, radius - rim.Thickness / 2, radius - rim.Thickness / 2);
            }

            var center = new Point(size / 2.0, size / 2.0);
            var amber = new LinearGradientBrush(
                Color.FromRgb(0xF4, 0xB8, 0x76),
                Color.FromRgb(0xE0, 0x93, 0x4A),
                new Point(0.2, 0),
                new Point(0.8, 1));

            var ringRadius = tile.Width * (small ? 0.27 : 0.255);
            var ringStroke = small ? Math.Max(1.6, size * 0.11) : tile.Width * 0.072;

            // Ring with an opening at the upper right (−45°), drawn as a 290° arc.
            var gapCenter = -45.0;
            var gapHalf = small ? 40.0 : 35.0;
            var startAngle = gapCenter + gapHalf;
            var endAngle = gapCenter - gapHalf + 360;
            var ring = new StreamGeometry();
            using (var g = ring.Open())
            {
                g.BeginFigure(PointOn(center, ringRadius, startAngle), false, false);
                g.ArcTo(PointOn(center, ringRadius, endAngle), new Size(ringRadius, ringRadius), 0, true, SweepDirection.Clockwise, true, true);
            }

            dc.DrawGeometry(null, new Pen(amber, ringStroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, ring);

            // Core dot.
            var coreRadius = small ? Math.Max(1.4, size * 0.085) : tile.Width * 0.062;
            dc.DrawEllipse(amber, null, center, coreRadius, coreRadius);

            // Signal dot in the opening – the "pilot" light.
            var signalRadius = small ? Math.Max(1.2, size * 0.07) : tile.Width * 0.046;
            var signalDistance = ringRadius + (small ? 0 : tile.Width * 0.035);
            var signal = PointOn(center, signalDistance, gapCenter);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xF7, 0xE6, 0xD2)), null, signal, signalRadius, signalRadius);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        RenderOptions.SetEdgeMode(visual, EdgeMode.Unspecified);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// The 1280×640 card GitHub and X show when the repository link is shared.
    /// </summary>
    private static BitmapSource RenderSocialCard(string screenshotPath)
    {
        const int width = 1280, height = 640;
        var display = new Typeface(new FontFamily("Segoe UI Variable Display"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var text = new Typeface(new FontFamily("Segoe UI Variable Text"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var mono = new Typeface(new FontFamily("Cascadia Mono"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var primary = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xEF));
        var secondary = new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xA9));
        var amber = new SolidColorBrush(Color.FromRgb(0xE9, 0xA3, 0x5C));

        FormattedText Text(string value, Typeface face, double size, Brush brush, double maxWidth = 0)
        {
            var formatted = new FormattedText(value, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush, 1);
            if (maxWidth > 0)
            {
                formatted.MaxTextWidth = maxWidth;
            }

            return formatted;
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x1A, 0x1A, 0x1D), Color.FromRgb(0x0F, 0x0F, 0x11), 45), null, new Rect(0, 0, width, height));
            var glow = new RadialGradientBrush(Color.FromArgb(46, 0xE9, 0xA3, 0x5C), Color.FromArgb(0, 0xE9, 0xA3, 0x5C)) { Center = new Point(0.05, 1), GradientOrigin = new Point(0.05, 1), RadiusX = 0.6, RadiusY = 1.0 };
            dc.DrawRectangle(glow, null, new Rect(0, 0, width, height));

            // The app, bleeding off the right edge.
            var shot = new BitmapImage();
            shot.BeginInit();
            shot.UriSource = new Uri(Path.GetFullPath(screenshotPath));
            shot.CacheOption = BitmapCacheOption.OnLoad;
            shot.EndInit();
            var shotWidth = 760.0;
            var frame = new Rect(588, 104, shotWidth, shotWidth * shot.PixelHeight / shot.PixelWidth);
            var shadow = new RadialGradientBrush(Color.FromArgb(140, 0, 0, 0), Color.FromArgb(0, 0, 0, 0)) { RadiusX = 0.6, RadiusY = 0.6 };
            dc.DrawRectangle(shadow, null, new Rect(frame.X - 60, frame.Y - 20, frame.Width + 120, frame.Height + 100));
            dc.PushClip(new RectangleGeometry(frame, 14, 14));
            dc.DrawImage(shot, frame);
            dc.Pop();
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1), frame, 14, 14);

            // Identity and pitch.
            dc.DrawImage(Render(192), new Rect(64, 92, 96, 96));
            dc.DrawText(Text("PortPilot", display, 64, primary), new Point(72, 206));
            dc.DrawText(Text("Something is already running on port 3000.", display, 30, primary, 480), new Point(74, 300));
            dc.DrawText(Text("A native Windows app that tells you what it is, and lets you end it with dignity.", text, 21, secondary, 470), new Point(74, 392));
            dc.DrawText(Text("Windows 10 · 11   ·   native WPF   ·   no Electron", mono, 15, amber), new Point(74, 548));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static Point PointOn(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void WriteIco(string path, IReadOnlyList<(int Size, byte[] Png)> frames)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0); // reserved
        writer.Write((ushort)1); // type: icon
        writer.Write((ushort)frames.Count);

        var offset = 6 + 16 * frames.Count;
        foreach (var (size, png) in frames)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0); // palette
            writer.Write((byte)0); // reserved
            writer.Write((ushort)1); // planes
            writer.Write((ushort)32); // bpp
            writer.Write(png.Length);
            writer.Write(offset);
            offset += png.Length;
        }

        foreach (var (_, png) in frames)
        {
            writer.Write(png);
        }
    }
}
