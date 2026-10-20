using System.Windows;
using System.Windows.Media;

namespace FileWheel.UI.Editors;

/// <summary>The swatches of the "Add BG" panel: soft mesh gradients on top, solid colors below.</summary>
public static class Backgrounds
{
    public sealed record Swatch(string Name, Func<Brush> Make);

    static Brush Mesh(string baseFrom, string baseTo, double angle, params (string Color, double X, double Y, double R)[] blobs)
    {
        var group = new DrawingGroup();
        var bounds = new Rect(0, 0, 1, 1);
        var linear = new LinearGradientBrush(Theme.Hex(baseFrom), Theme.Hex(baseTo), angle);
        group.Children.Add(new GeometryDrawing(linear, null, new RectangleGeometry(bounds)));
        foreach (var (color, x, y, r) in blobs)
        {
            var c = Theme.Hex(color);
            var radial = new RadialGradientBrush
            {
                Center = new Point(x, y),
                GradientOrigin = new Point(x, y),
                RadiusX = r,
                RadiusY = r,
            };
            radial.GradientStops.Add(new GradientStop(c, 0));
            radial.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
            group.Children.Add(new GeometryDrawing(radial, null, new RectangleGeometry(bounds)));
        }
        var brush = new DrawingBrush(group) { Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }

    static Brush Solid(string hex)
    {
        var b = new SolidColorBrush(Theme.Hex(hex));
        b.Freeze();
        return b;
    }

    public static readonly Swatch[] Gradients =
    [
        new("Tangerine", () => Mesh("#F9A23A", "#EE4D5A", 120, ("#FFD15A", 0.85, 0.12, 0.6), ("#F04A6E", 0.1, 0.55, 0.55), ("#8F7CF2", 0.9, 0.95, 0.6), ("#FF8A3D", 0.45, 0.35, 0.45))),
        new("Pearl", () => Mesh("#FFFFFF", "#ECEFF4", 90, ("#FFD9E8", 0.15, 0.2, 0.55), ("#D6E6FF", 0.85, 0.85, 0.6))),
        new("Prism", () => Mesh("#FBC2EB", "#A6C1EE", 135, ("#FFF1A8", 0.15, 0.15, 0.5), ("#B5F5D8", 0.85, 0.9, 0.55))),
        new("Lilac", () => Mesh("#F3E7FF", "#DCCBFF", 90, ("#FFFFFF", 0.3, 0.25, 0.5), ("#FBD3E9", 0.8, 0.8, 0.5))),
        new("Rose", () => Mesh("#F78CC6", "#B06AF0", 135, ("#FFC3D9", 0.2, 0.15, 0.5))),
        new("Ember", () => Mesh("#25124F", "#0F0A26", 90, ("#F25F2B", 0.2, 0.85, 0.6), ("#7D3CF0", 0.85, 0.2, 0.55), ("#F2B33A", 0.75, 0.9, 0.35))),
        new("Lagoon", () => Mesh("#1BB7C9", "#2F66E0", 120, ("#7CF0C8", 0.15, 0.15, 0.5))),
        new("Sky", () => Mesh("#8FE3FF", "#4E8DF5", 110, ("#E3F7FF", 0.2, 0.2, 0.5))),
        new("Haze", () => Mesh("#F7F1FF", "#E7E4FB", 90, ("#E0F0FF", 0.8, 0.2, 0.5), ("#FFE8F4", 0.2, 0.85, 0.5))),
    ];

    public static readonly Swatch[] Solids =
    [
        new("White", () => Solid("#FFFFFF")),
        new("Sun", () => Solid("#F6C54A")),
        new("Lime", () => Solid("#A2CF3E")),
        new("Leaf", () => Solid("#38A55A")),
        new("Jade", () => Solid("#15A08A")),
        new("Teal", () => Solid("#22B3B5")),
        new("Sky", () => Solid("#3BA6F2")),
        new("Cobalt", () => Solid("#2F6AE0")),
        new("Ink", () => Solid("#2A3360")),
        new("Graphite", () => Solid("#34373E")),
    ];
}
