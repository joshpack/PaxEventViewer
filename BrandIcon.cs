using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace pxdEventViewer;

/// <summary>
/// pxd Event Viewer's icon: a log sheet with a yellow warning sign on an orange-to-red tile.
/// Drawn from vector shapes so it stays crisp at every size; used for the window and the exe's .ico.
/// </summary>
public static class BrandIcon
{
    public static Control Build(double size)
    {
        var a = new Canvas { Width = 100, Height = 100 };
        void Fill(string data, string color, string? stroke = null, double width = 0) =>
            a.Children.Add(new ShapePath
            {
                Data = Geometry.Parse(data), Fill = new SolidColorBrush(Color.Parse(color)),
                Stroke = stroke is null ? null : new SolidColorBrush(Color.Parse(stroke)), StrokeThickness = width, StrokeJoin = PenLineJoin.Round,
            });
        void Line(string data, string color, double width) =>
            a.Children.Add(new ShapePath { Data = Geometry.Parse(data), Stroke = new SolidColorBrush(Color.Parse(color)), StrokeThickness = width, StrokeLineCap = PenLineCap.Round });

        // The log sheet with its lines of entries.
        Fill("M24,8 L66,8 Q72,8 72,14 L72,80 Q72,86 66,86 L24,86 Q18,86 18,80 L18,14 Q18,8 24,8 Z", "#F3F4FA");
        Line("M28,24 L62,24", "#3A3F52", 6);
        Line("M28,37 L62,37", "#3A3F52", 6);
        Line("M28,50 L48,50", "#3A3F52", 6);
        Line("M28,63 L40,63", "#3A3F52", 6);
        // The warning sign over its corner.
        Fill("M63,44 L93,93 L33,93 Z", "#FFD23F", "#1B1D24", 5);
        Line("M63,61 L63,76", "#1B1D24", 7);
        a.Children.Add(new ShapePath { Data = new EllipseGeometry(new Rect(59, 81, 8, 8)), Fill = new SolidColorBrush(Color.Parse("#1B1D24")) });
        return Tile(size, "#FF9A4D", "#C8203A", a, 0.11);
    }

    public static RenderTargetBitmap Render(int size)
    {
        var c = Build(size);
        c.Measure(new Size(size, size));
        c.Arrange(new Rect(0, 0, size, size));
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        bitmap.Render(c);
        return bitmap;
    }

    public static WindowIcon Window(int size = 64) => new(Render(size));

    /// <summary>Writes a multi-size .ico (PNG entries) for the exe, or a 256 px PNG when the path ends in .png.</summary>
    public static void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            using var png = File.Create(path);
            Render(256).Save(png, PngBitmapEncoderOptions.Default);
            return;
        }

        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var images = sizes.Select(s =>
        {
            using var ms = new MemoryStream();
            Render(s).Save(ms, PngBitmapEncoderOptions.Default);
            return ms.ToArray();
        }).ToList();
        using var w = new BinaryWriter(File.Create(path));
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            var b = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
            w.Write(b);
            w.Write(b);
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(images[i].Length);
            w.Write(offset);
            offset += images[i].Length;
        }

        foreach (var img in images) w.Write(img);
    }

    private static Grid Tile(double size, string from, string to, Canvas art, double margin)
    {
        var g = new Grid { Width = size, Height = size };
        g.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(size * 0.22),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse(from), 0), new GradientStop(Color.Parse(to), 1) },
            },
        });
        g.Children.Add(new Viewbox { Margin = new Thickness(size * margin), Child = art });
        return g;
    }
}
