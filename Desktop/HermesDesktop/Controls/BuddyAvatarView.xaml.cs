using Hermes.Agent.Buddy;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace HermesDesktop.Controls;

public sealed partial class BuddyAvatarView : UserControl
{
    private const double CanvasSize = 260;

    public BuddyAvatarView()
    {
        InitializeComponent();
    }

    public void SetBuddy(Buddy buddy)
    {
        ArgumentNullException.ThrowIfNull(buddy);

        AvatarCanvas.Children.Clear();
        var palette = PaletteFor(buddy.Palette, buddy.IsShiny);
        DrawStage(palette, buddy.IsShiny);
        DrawSpeciesSilhouette(buddy, palette);
        DrawFace(buddy, palette);
        DrawAccessory(buddy.Hat, palette);
    }

    private void DrawStage(AvatarPalette palette, bool shiny)
    {
        AddEllipse(18, 20, 224, 224, palette.Aura, opacity: shiny ? 0.42 : 0.28);
        AddEllipse(48, 205, 164, 26, ColorHelper.FromArgb(90, 0, 0, 0));

        if (shiny)
        {
            AddEllipse(24, 26, 212, 212, palette.Highlight, thickness: 3, opacity: 0.82);
            AddEllipse(44, 46, 172, 172, palette.Highlight, thickness: 1.5, opacity: 0.35);
        }
    }

    private void DrawSpeciesSilhouette(Buddy buddy, AvatarPalette palette)
    {
        var species = buddy.Species.ToLowerInvariant();
        switch (species)
        {
            case "cat":
                AddTriangle(new(74, 96), new(96, 48), new(118, 94), palette.BodyDark);
                AddTriangle(new(142, 94), new(164, 48), new(186, 96), palette.BodyDark);
                AddEllipse(64, 76, 132, 128, palette.Body);
                AddTail(palette);
                break;
            case "dog":
                AddEllipse(58, 78, 144, 128, palette.Body);
                AddEllipse(42, 90, 54, 82, palette.BodyDark);
                AddEllipse(164, 90, 54, 82, palette.BodyDark);
                AddEllipse(82, 148, 96, 52, palette.Accent, opacity: 0.72);
                break;
            case "bird":
            case "phoenix":
                AddWing(26, 106, palette.BodyDark);
                AddWing(156, 106, palette.BodyDark, flip: true);
                AddEllipse(70, 62, 120, 142, palette.Body);
                AddTriangle(new(126, 126), new(158, 136), new(126, 146), palette.Highlight);
                if (species == "phoenix")
                    AddFlameCrest(palette);
                break;
            case "fish":
                AddTriangle(new(58, 128), new(24, 92), new(24, 164), palette.BodyDark);
                AddEllipse(58, 78, 152, 104, palette.Body);
                AddTriangle(new(182, 130), new(226, 102), new(226, 158), palette.Accent);
                break;
            case "dragon":
            case "griffin":
                AddWing(26, 86, palette.BodyDark);
                AddWing(156, 86, palette.BodyDark, flip: true);
                AddTriangle(new(96, 78), new(130, 30), new(164, 78), palette.Accent);
                AddEllipse(62, 78, 136, 130, palette.Body);
                AddTriangle(new(130, 202), new(154, 232), new(106, 232), palette.BodyDark);
                break;
            case "unicorn":
                AddTriangle(new(130, 32), new(146, 88), new(114, 88), palette.Highlight);
                AddEllipse(64, 76, 132, 130, palette.Body);
                AddEllipse(48, 94, 46, 76, palette.BodyDark);
                AddEllipse(166, 94, 46, 76, palette.BodyDark);
                break;
            case "cosmic":
            case "quantum":
            case "void":
            case "star":
                AddStar(130, 124, 78, 38, palette.Body);
                AddEllipse(78, 76, 104, 104, palette.Accent, opacity: 0.45);
                break;
            case "cube":
                AddRectangle(66, 66, 128, 128, 24, palette.Body);
                AddRectangle(88, 88, 84, 22, 11, palette.Accent, opacity: 0.35);
                break;
            case "dot":
                AddEllipse(82, 74, 96, 96, palette.Body);
                AddEllipse(102, 94, 56, 56, palette.Accent, opacity: 0.4);
                break;
            case "line":
                AddRectangle(54, 96, 152, 70, 35, palette.Body);
                AddRectangle(80, 116, 100, 12, 6, palette.Accent, opacity: 0.48);
                break;
            default:
                AddEllipse(58, 66, 144, 138, palette.Body);
                AddEllipse(82, 92, 96, 82, palette.Accent, opacity: 0.35);
                break;
        }
    }

    private void DrawFace(Buddy buddy, AvatarPalette palette)
    {
        var eyes = buddy.Eyes.ToLowerInvariant();
        var left = new Point(104, 120);
        var right = new Point(156, 120);

        switch (eyes)
        {
            case "sleepy":
                AddLine(left.X - 10, left.Y, left.X + 10, left.Y, palette.Ink, 4);
                AddLine(right.X - 10, right.Y, right.X + 10, right.Y, palette.Ink, 4);
                break;
            case "wide":
                AddEye(left, 15, palette);
                AddEye(right, 15, palette);
                break;
            case "excited":
            case "sparkly":
                AddStar(left.X, left.Y, 16, 8, palette.Highlight);
                AddStar(right.X, right.Y, 16, 8, palette.Highlight);
                break;
            case "curious":
                AddEye(left, 9, palette);
                AddEye(right, 14, palette);
                break;
            case "determined":
                AddLine(92, 112, 116, 122, palette.Ink, 4);
                AddLine(144, 122, 168, 112, palette.Ink, 4);
                break;
            case "tired":
                AddLine(94, 124, 114, 118, palette.Ink, 4);
                AddLine(146, 118, 166, 124, palette.Ink, 4);
                break;
            default:
                AddEye(left, 11, palette);
                AddEye(right, 11, palette);
                break;
        }

        AddLine(112, 158, 130, 166, palette.Ink, 3);
        AddLine(130, 166, 148, 158, palette.Ink, 3);
    }

    private void DrawAccessory(string hat, AvatarPalette palette)
    {
        switch (hat.ToLowerInvariant())
        {
            case "cap":
                AddRectangle(88, 62, 84, 28, 12, palette.Highlight);
                AddRectangle(150, 78, 34, 10, 5, palette.Highlight);
                break;
            case "beanie":
                AddEllipse(88, 52, 84, 42, palette.Highlight);
                AddEllipse(120, 40, 22, 22, palette.Accent);
                break;
            case "bow":
                AddTriangle(new(104, 66), new(132, 84), new(104, 102), palette.Highlight);
                AddTriangle(new(156, 66), new(128, 84), new(156, 102), palette.Highlight);
                AddEllipse(121, 76, 18, 18, palette.Accent);
                break;
            case "crown":
                AddTriangle(new(88, 88), new(104, 46), new(122, 88), palette.Highlight);
                AddTriangle(new(116, 88), new(132, 38), new(148, 88), palette.Highlight);
                AddTriangle(new(140, 88), new(158, 46), new(176, 88), palette.Highlight);
                AddRectangle(88, 82, 88, 18, 5, palette.Highlight);
                break;
            case "wizard":
                AddTriangle(new(92, 92), new(132, 24), new(174, 92), palette.Highlight);
                AddRectangle(92, 86, 82, 12, 6, palette.Accent);
                break;
            case "halo":
                AddEllipse(94, 42, 72, 22, palette.Highlight, thickness: 5);
                break;
            case "headphones":
                AddEllipse(82, 90, 28, 54, palette.Highlight);
                AddEllipse(150, 90, 28, 54, palette.Highlight);
                AddLine(96, 84, 130, 60, palette.Highlight, 6);
                AddLine(130, 60, 164, 84, palette.Highlight, 6);
                break;
        }
    }

    private void AddEye(Point center, double radius, AvatarPalette palette)
    {
        AddEllipse(center.X - radius, center.Y - radius, radius * 2, radius * 2, ColorHelper.FromArgb(255, 244, 248, 252));
        AddEllipse(center.X - radius * 0.45, center.Y - radius * 0.35, radius * 0.9, radius * 0.9, palette.Ink);
    }

    private void AddTail(AvatarPalette palette)
    {
        AddEllipse(174, 142, 44, 76, palette.BodyDark);
        AddEllipse(182, 148, 32, 52, palette.Aura);
    }

    private void AddWing(double x, double y, Windows.UI.Color fill, bool flip = false)
    {
        var points = flip
            ? new[] { new Point(x + 78, y), new Point(x, y + 34), new Point(x + 68, y + 72) }
            : new[] { new Point(x, y), new Point(x + 78, y + 34), new Point(x + 10, y + 72) };
        AddPolygon(points, fill, opacity: 0.82);
    }

    private void AddFlameCrest(AvatarPalette palette)
    {
        AddTriangle(new(108, 78), new(126, 36), new(138, 86), palette.Highlight);
        AddTriangle(new(126, 84), new(148, 44), new(156, 92), palette.Accent);
    }

    private void AddStar(double centerX, double centerY, double outer, double inner, Windows.UI.Color fill)
    {
        var points = new Point[10];
        for (var i = 0; i < points.Length; i++)
        {
            var angle = -Math.PI / 2 + i * Math.PI / 5;
            var radius = i % 2 == 0 ? outer : inner;
            points[i] = new Point(centerX + Math.Cos(angle) * radius, centerY + Math.Sin(angle) * radius);
        }
        AddPolygon(points, fill);
    }

    private void AddEllipse(
        double x,
        double y,
        double width,
        double height,
        Windows.UI.Color fill,
        double thickness = 0,
        double opacity = 1)
    {
        var ellipse = new Ellipse
        {
            Width = width,
            Height = height,
            Opacity = opacity
        };

        if (thickness > 0)
        {
            ellipse.Stroke = new SolidColorBrush(fill);
            ellipse.StrokeThickness = thickness;
            ellipse.Fill = new SolidColorBrush(Colors.Transparent);
        }
        else
        {
            ellipse.Fill = new SolidColorBrush(fill);
        }

        Canvas.SetLeft(ellipse, x);
        Canvas.SetTop(ellipse, y);
        AvatarCanvas.Children.Add(ellipse);
    }

    private void AddRectangle(
        double x,
        double y,
        double width,
        double height,
        double radius,
        Windows.UI.Color fill,
        double opacity = 1)
    {
        var rect = new Rectangle
        {
            Width = width,
            Height = height,
            RadiusX = radius,
            RadiusY = radius,
            Fill = new SolidColorBrush(fill),
            Opacity = opacity
        };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        AvatarCanvas.Children.Add(rect);
    }

    private void AddTriangle(Point a, Point b, Point c, Windows.UI.Color fill) =>
        AddPolygon(new[] { a, b, c }, fill);

    private void AddPolygon(IEnumerable<Point> points, Windows.UI.Color fill, double opacity = 1)
    {
        var polygon = new Polygon
        {
            Fill = new SolidColorBrush(fill),
            Opacity = opacity
        };

        foreach (var point in points)
            polygon.Points.Add(point);

        AvatarCanvas.Children.Add(polygon);
    }

    private void AddLine(
        double x1,
        double y1,
        double x2,
        double y2,
        Windows.UI.Color stroke,
        double thickness)
    {
        AvatarCanvas.Children.Add(new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = new SolidColorBrush(stroke),
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });
    }

    private static AvatarPalette PaletteFor(string paletteName, bool shiny)
    {
        var palette = BuddyPalettes.Normalize(paletteName) switch
        {
            BuddyPalettes.Tide => new AvatarPalette(
                ColorHelper.FromArgb(255, 81, 169, 196),
                ColorHelper.FromArgb(255, 34, 105, 130),
                ColorHelper.FromArgb(255, 234, 194, 111),
                ColorHelper.FromArgb(255, 139, 232, 229),
                ColorHelper.FromArgb(255, 15, 28, 36),
                ColorHelper.FromArgb(42, 81, 169, 196)),
            BuddyPalettes.Moss => new AvatarPalette(
                ColorHelper.FromArgb(255, 111, 177, 105),
                ColorHelper.FromArgb(255, 60, 112, 72),
                ColorHelper.FromArgb(255, 205, 145, 96),
                ColorHelper.FromArgb(255, 179, 231, 137),
                ColorHelper.FromArgb(255, 16, 34, 25),
                ColorHelper.FromArgb(44, 111, 177, 105)),
            BuddyPalettes.Ember => new AvatarPalette(
                ColorHelper.FromArgb(255, 225, 112, 78),
                ColorHelper.FromArgb(255, 149, 57, 65),
                ColorHelper.FromArgb(255, 72, 166, 161),
                ColorHelper.FromArgb(255, 255, 207, 118),
                ColorHelper.FromArgb(255, 45, 24, 23),
                ColorHelper.FromArgb(46, 225, 112, 78)),
            BuddyPalettes.Violet => new AvatarPalette(
                ColorHelper.FromArgb(255, 158, 124, 218),
                ColorHelper.FromArgb(255, 84, 65, 143),
                ColorHelper.FromArgb(255, 91, 192, 181),
                ColorHelper.FromArgb(255, 224, 196, 255),
                ColorHelper.FromArgb(255, 27, 23, 45),
                ColorHelper.FromArgb(46, 158, 124, 218)),
            BuddyPalettes.Mono => new AvatarPalette(
                ColorHelper.FromArgb(255, 181, 190, 201),
                ColorHelper.FromArgb(255, 91, 101, 113),
                ColorHelper.FromArgb(255, 212, 160, 23),
                ColorHelper.FromArgb(255, 238, 242, 246),
                ColorHelper.FromArgb(255, 20, 25, 31),
                ColorHelper.FromArgb(36, 181, 190, 201)),
            _ => new AvatarPalette(
                ColorHelper.FromArgb(255, 212, 160, 23),
                ColorHelper.FromArgb(255, 140, 103, 29),
                ColorHelper.FromArgb(255, 76, 166, 173),
                ColorHelper.FromArgb(255, 255, 225, 119),
                ColorHelper.FromArgb(255, 36, 28, 16),
                ColorHelper.FromArgb(46, 212, 160, 23))
        };

        return shiny
            ? palette with { Highlight = ColorHelper.FromArgb(255, 255, 244, 156), Aura = ColorHelper.FromArgb(76, 255, 226, 109) }
            : palette;
    }

    private sealed record AvatarPalette(
        Windows.UI.Color Body,
        Windows.UI.Color BodyDark,
        Windows.UI.Color Accent,
        Windows.UI.Color Highlight,
        Windows.UI.Color Ink,
        Windows.UI.Color Aura);
}
