// Renders the PortPilot mark into a multi-resolution .ico and preview PNGs.
// Usage: dotnet run --project tools/PortPilot.IconGen -- <output-directory>

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
