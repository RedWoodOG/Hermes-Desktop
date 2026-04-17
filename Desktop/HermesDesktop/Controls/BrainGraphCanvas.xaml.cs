using System.Numerics;
using HermesDesktop.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI;
using Windows.UI;

namespace HermesDesktop.Controls;

public sealed partial class BrainGraphCanvas : UserControl
{
    private BrainGraphData? _graph;
    private int _frame;
    private int _minimapInvalidateCounter;

    // Pre-created text formats
    private CanvasTextFormat? _projectLabelFormat;
    private CanvasTextFormat? _ghostLabelFormat;
    private CanvasTextFormat? _nodeLabelFormat;
    private CanvasTextFormat? _sublabelFormat;
    private CanvasTextFormat? _minimapLabelFormat;

    // Node type colors
    private static readonly Dictionary<BrainNodeType, (Color Main, Color Glow)> NodeColors = new()
    {
        [BrainNodeType.Project]   = (ParseHex("#D4A017"), ParseHex("#30D4A017")),
        [BrainNodeType.Session]   = (ParseHex("#49C27D"), ParseHex("#2049C27D")),
        [BrainNodeType.Memory]    = (ParseHex("#818CF8"), ParseHex("#20818CF8")),
        [BrainNodeType.Tool]      = (ParseHex("#FF8A65"), ParseHex("#20FF8A65")),
        [BrainNodeType.Skill]     = (ParseHex("#E05555"), ParseHex("#20E05555")),
        [BrainNodeType.Workspace] = (ParseHex("#2A3545"), ParseHex("#102A3545")),
        [BrainNodeType.File]      = (ParseHex("#5A7555"), ParseHex("#205A7555")),
    };

    public BrainGraphCanvas()
    {
        InitializeComponent();
    }

    public void SetGraphData(BrainGraphData graph)
    {
        _graph = graph;
        _frame = 0;
        MinimapCanvas?.Invalidate();
    }

    private void OnCreateResources(CanvasAnimatedControl sender, CanvasCreateResourcesEventArgs args)
    {
        _projectLabelFormat = new CanvasTextFormat
        {
            FontFamily = "ms-appx:///Assets/Fonts/JetBrainsMono.ttf#JetBrains Mono",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
        };
        _ghostLabelFormat = new CanvasTextFormat
        {
            FontFamily = "ms-appx:///Assets/Fonts/JetBrainsMono.ttf#JetBrains Mono",
            FontSize = 9,
            FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
        };
        _nodeLabelFormat = new CanvasTextFormat
        {
            FontFamily = "ms-appx:///Assets/Fonts/Inter-Regular.ttf#Inter",
            FontSize = 9,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
        };
        _sublabelFormat = new CanvasTextFormat
        {
            FontFamily = "ms-appx:///Assets/Fonts/JetBrainsMono.ttf#JetBrains Mono",
            FontSize = 8,
            FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
        };
        _minimapLabelFormat = new CanvasTextFormat
        {
            FontFamily = "ms-appx:///Assets/Fonts/JetBrainsMono.ttf#JetBrains Mono",
            FontSize = 7,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
        };
    }

    private void OnGraphDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        if (_graph is null) return;
        var ds = args.DrawingSession;
        _frame++;

        float w = (float)sender.Size.Width;
        float h = (float)sender.Size.Height;
        float cx = w / 2f;
        float cy = h / 2f;

        // Animate nodes
        AnimateNodes();

        // Draw edges
        foreach (var edge in _graph.Edges)
        {
            var a = _graph.GetNode(edge.FromId);
            var b = _graph.GetNode(edge.ToId);
            if (a is null || b is null) continue;

            DrawEdge(ds, a, b, edge, cx, cy);
        }

        // Draw nodes
        foreach (var node in _graph.Nodes)
        {
            DrawNode(ds, node, cx, cy);
        }

        // Draw particles
        foreach (var edge in _graph.Edges)
        {
            var a = _graph.GetNode(edge.FromId);
            var b = _graph.GetNode(edge.ToId);
            if (a is null || b is null) continue;
            if (a.IsGhost || b.IsGhost) continue;
            if (edge.Strength < 0.3f) continue;

            DrawParticle(ds, a, b, edge, cx, cy);
        }

        // Periodically invalidate minimap
        _minimapInvalidateCounter++;
        if (_minimapInvalidateCounter % 60 == 0)
            MinimapCanvas?.Invalidate();
    }

    private void AnimateNodes()
    {
        if (_graph is null) return;
        for (int i = 0; i < _graph.Nodes.Count; i++)
        {
            var n = _graph.Nodes[i];
            float speed = 0.0003f + (i % 5) * 0.0001f;
            float amp = n.Type == BrainNodeType.Project ? 2f : (n.IsGhost ? 3f : 5f);
            n.X = n.BaseX + MathF.Sin(_frame * speed + i * 1.7f) * amp;
            n.Y = n.BaseY + MathF.Cos(_frame * speed * 0.7f + i * 2.3f) * amp;
        }
    }

    private void DrawEdge(CanvasDrawingSession ds, BrainNode a, BrainNode b, BrainEdge edge, float cx, float cy)
    {
        bool isGhost = a.IsGhost || b.IsGhost;
        float baseAlpha = isGhost ? edge.Strength * 0.4f : edge.Strength * 0.5f;

        // Check if linked to the most recent session (first session node)
        bool isActive = (a.Type == BrainNodeType.Session && a.Id == _graph!.Nodes.FirstOrDefault(n => n.Type == BrainNodeType.Session)?.Id) ||
                        (b.Type == BrainNodeType.Session && b.Id == _graph!.Nodes.FirstOrDefault(n => n.Type == BrainNodeType.Session)?.Id);

        float ax = cx + a.X, ay = cy + a.Y;
        float bx = cx + b.X, by = cy + b.Y;

        // Bezier control point for organic curve
        float mx = (ax + bx) / 2f + (ay - by) * 0.1f;
        float my = (ay + by) / 2f + (bx - ax) * 0.1f;

        using var pathBuilder = new CanvasPathBuilder(ds);
        pathBuilder.BeginFigure(ax, ay);
        pathBuilder.AddQuadraticBezier(new Vector2(mx, my), new Vector2(bx, by));
        pathBuilder.EndFigure(CanvasFigureLoop.Open);
        using var path = CanvasGeometry.CreatePath(pathBuilder);

        Color edgeColor;
        float lineWidth;

        if (isActive)
        {
            float pulse = 0.3f + MathF.Sin(_frame * 0.03f) * 0.15f;
            edgeColor = WithAlpha(NodeColors[BrainNodeType.Project].Main, baseAlpha + pulse);
            lineWidth = 1.5f;
        }
        else
        {
            var typeColor = NodeColors.GetValueOrDefault(a.Type, NodeColors[BrainNodeType.Workspace]).Main;
            edgeColor = WithAlpha(typeColor, baseAlpha);
            lineWidth = isGhost ? 0.5f : 1f;
        }

        ds.DrawGeometry(path, edgeColor, lineWidth);
    }

    private void DrawNode(CanvasDrawingSession ds, BrainNode node, float cx, float cy)
    {
        var (mainColor, glowColor) = NodeColors.GetValueOrDefault(node.Type, NodeColors[BrainNodeType.Workspace]);
        float size = node.Size;
        float nx = cx + node.X;
        float ny = cy + node.Y;
        bool isCenter = node.Type == BrainNodeType.Project;

        // Glow (non-ghost only)
        if (!node.IsGhost)
        {
            using var glow = new CanvasRadialGradientBrush(ds, glowColor, Microsoft.UI.Colors.Transparent);
            glow.Center = new Vector2(nx, ny);
            glow.RadiusX = size * 2.5f;
            glow.RadiusY = size * 2.5f;
            ds.FillEllipse(nx, ny, size * 2.5f, size * 2.5f, glow);
        }

        if (isCenter)
        {
            // Pulsing center node
            float pulse = 0.7f + MathF.Sin(_frame * 0.02f) * 0.3f;
            ds.FillCircle(nx, ny, size / 2f, WithAlpha(mainColor, pulse * 0.3f));
            ds.DrawCircle(nx, ny, size / 2f, WithAlpha(mainColor, pulse * 0.6f), 1.5f);

            // Bright inner core
            ds.FillCircle(nx, ny, size / 5f, WithAlpha(mainColor, pulse));
        }
        else if (node.IsGhost)
        {
            ds.FillCircle(nx, ny, size / 2f, WithAlpha(mainColor, 0.15f));
            ds.DrawCircle(nx, ny, size / 2f, WithAlpha(mainColor, 0.2f), 0.5f);
        }
        else
        {
            ds.FillCircle(nx, ny, size / 2f, WithAlpha(mainColor, 0.25f));
            ds.DrawCircle(nx, ny, size / 2f, WithAlpha(mainColor, 0.5f), 1f);
        }

        // Labels
        bool showLabel = isCenter || node.Type == BrainNodeType.Session ||
                         node.Type == BrainNodeType.Workspace || node.Type == BrainNodeType.Skill ||
                         node.Type == BrainNodeType.Memory;

        if (showLabel)
        {
            var format = isCenter ? _projectLabelFormat : (node.IsGhost ? _ghostLabelFormat : _nodeLabelFormat);
            var labelColor = node.IsGhost
                ? WithAlpha(mainColor, 0.3f)
                : WithAlpha(ParseHex("#C0C8D4"), isCenter ? 1f : 0.7f);

            if (format is not null)
                ds.DrawText(node.Label, nx, ny + size / 2f + 10f, labelColor, format);

            if (node.Sublabel is not null && !node.IsGhost && _sublabelFormat is not null)
            {
                ds.DrawText(node.Sublabel, nx, ny + size / 2f + 22f,
                    WithAlpha(ParseHex("#4A5565"), 0.7f), _sublabelFormat);
            }
        }
    }

    private void DrawParticle(CanvasDrawingSession ds, BrainNode a, BrainNode b, BrainEdge edge, float cx, float cy)
    {
        float progress = (_frame * 0.005f * edge.Strength + edge.Strength * 100f) % 1f;
        float px = cx + a.X + (b.X - a.X) * progress;
        float py = cy + a.Y + (b.Y - a.Y) * progress;

        var typeColor = NodeColors.GetValueOrDefault(b.Type, NodeColors[BrainNodeType.Workspace]).Main;
        ds.FillCircle(px, py, 1.5f, WithAlpha(typeColor, 0.6f));
    }

    // ── Minimap ──

    private void OnMinimapDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_graph is null) return;
        var ds = args.DrawingSession;
        float scale = 0.12f;
        float ox = 60f, oy = 40f;

        // Edges
        foreach (var edge in _graph.Edges)
        {
            var a = _graph.GetNode(edge.FromId);
            var b = _graph.GetNode(edge.ToId);
            if (a is null || b is null) continue;

            ds.DrawLine(
                ox + a.BaseX * scale, oy + a.BaseY * scale,
                ox + b.BaseX * scale, oy + b.BaseY * scale,
                WithAlpha(ParseHex("#2A3545"), 0.3f), 0.5f);
        }

        // Nodes
        foreach (var node in _graph.Nodes)
        {
            var (mainColor, _) = NodeColors.GetValueOrDefault(node.Type, NodeColors[BrainNodeType.Workspace]);
            float px = ox + node.BaseX * scale;
            float py = oy + node.BaseY * scale;
            float sz = node.Type == BrainNodeType.Project ? 3f : (node.IsGhost ? 2f : 1.5f);

            ds.FillCircle(px, py, sz, WithAlpha(mainColor, node.IsGhost ? 0.3f : 0.6f));
        }

        // Viewport rectangle
        ds.DrawRectangle(ox - 25, oy - 18, 50, 36, WithAlpha(ParseHex("#D4A017"), 0.25f), 1f);
    }

    // ── Helpers ──

    private static Color WithAlpha(Color c, float alpha) =>
        Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B);

    private static Color ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 8)
        {
            return Color.FromArgb(
                byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber),
                byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber),
                byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber),
                byte.Parse(hex[6..8], System.Globalization.NumberStyles.HexNumber));
        }
        return Color.FromArgb(255,
            byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber),
            byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber),
            byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber));
    }
}
