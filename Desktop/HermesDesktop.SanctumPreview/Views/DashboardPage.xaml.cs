using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SanctumPreview.Models;

namespace SanctumPreview.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        BrainCanvas.SetGraphData(BuildDemoGraph());
    }

    private static BrainGraphData BuildDemoGraph()
    {
        var g = new BrainGraphData();
        const float cx = 0, cy = 0;

        // Center
        g.Nodes.Add(new BrainNode
        {
            Id = "hermes-cs",
            Type = BrainNodeType.Project,
            Label = "Hermes.CS",
            Sublabel = "v0.9.0 \u00B7 ONLINE",
            BaseX = cx, BaseY = cy,
        });

        // Sessions (inner ring, top)
        var sessions = new[]
        {
            ("s1", "UI Overhaul",  "39 turns \u00B7 active"),
            ("s2", "Greeting",     "39 turns \u00B7 5d ago"),
            ("s3", "Lifecycle",    "50 turns \u00B7 telegram"),
        };
        for (int i = 0; i < sessions.Length; i++)
        {
            var (id, label, sub) = sessions[i];
            float angle = -MathF.PI / 2f + (i - 1) * 0.7f;
            const float r = 140f;
            g.Nodes.Add(new BrainNode
            {
                Id = id, Type = BrainNodeType.Session, Label = label, Sublabel = sub,
                BaseX = cx + MathF.Cos(angle) * r,
                BaseY = cy + MathF.Sin(angle) * r,
            });
            g.Edges.Add(new BrainEdge { FromId = "hermes-cs", ToId = id, Strength = 0.8f });
        }

        // Memories (right cluster)
        var memories = new[] { ("m1", "Soul config"), ("m2", "User prefs"), ("m3", "Architecture"), ("m4", "WinUI patterns"), ("m5", "Dreamer logic") };
        for (int i = 0; i < memories.Length; i++)
        {
            var (id, label) = memories[i];
            float angle = 0.3f + (i - 2) * 0.45f;
            float r = 160f + (i % 2) * 40f;
            g.Nodes.Add(new BrainNode
            {
                Id = id, Type = BrainNodeType.Memory, Label = label,
                BaseX = cx + MathF.Cos(angle) * r,
                BaseY = cy + MathF.Sin(angle) * r,
            });
            g.Edges.Add(new BrainEdge { FromId = "hermes-cs", ToId = id, Strength = 0.4f });
        }
        g.Edges.Add(new BrainEdge { FromId = "m1", ToId = "m2", Strength = 0.2f });
        g.Edges.Add(new BrainEdge { FromId = "m3", ToId = "m4", Strength = 0.3f });
        g.Edges.Add(new BrainEdge { FromId = "m4", ToId = "m5", Strength = 0.15f });

        // Tools (bottom-left cluster)
        var tools = new[] { "file_read", "file_write", "web_search", "memory_recall", "wiki_search", "code_exec" };
        for (int i = 0; i < tools.Length; i++)
        {
            float angle = MathF.PI - 0.8f + i * 0.32f;
            float r = 140f + (i % 3) * 30f;
            var id = $"t{i + 1}";
            g.Nodes.Add(new BrainNode
            {
                Id = id, Type = BrainNodeType.Tool, Label = tools[i],
                BaseX = cx + MathF.Cos(angle) * r,
                BaseY = cy + MathF.Sin(angle) * r,
            });
            g.Edges.Add(new BrainEdge { FromId = "hermes-cs", ToId = id, Strength = 0.3f });
        }
        g.Edges.Add(new BrainEdge { FromId = "s1", ToId = "t1", Strength = 0.5f });
        g.Edges.Add(new BrainEdge { FromId = "s1", ToId = "t3", Strength = 0.4f });
        g.Edges.Add(new BrainEdge { FromId = "s3", ToId = "t2", Strength = 0.3f });
        g.Edges.Add(new BrainEdge { FromId = "s1", ToId = "t4", Strength = 0.3f });

        // Skills (bottom-right)
        var skills = new[] { ("sk1", "code-review"), ("sk2", "dreamer"), ("sk3", "summarize") };
        for (int i = 0; i < skills.Length; i++)
        {
            var (id, label) = skills[i];
            float angle = MathF.PI / 2f + 0.5f + i * 0.5f;
            const float r = 155f;
            g.Nodes.Add(new BrainNode
            {
                Id = id, Type = BrainNodeType.Skill, Label = label,
                BaseX = cx + MathF.Cos(angle) * r,
                BaseY = cy + MathF.Sin(angle) * r,
            });
            g.Edges.Add(new BrainEdge { FromId = "hermes-cs", ToId = id, Strength = 0.5f });
        }
        g.Edges.Add(new BrainEdge { FromId = "sk2", ToId = "m5", Strength = 0.4f });

        // Ghost workspaces
        var ghosts = new[]
        {
            ("ws1", "a9n-wiki",       -380f, -180f),
            ("ws2", "openclaw-core",  -340f,  140f),
            ("ws3", "aegis-desktop",   350f, -160f),
            ("ws4", "hermes-py",       380f,  120f),
            ("ws5", "atomic-bot",     -100f,  260f),
            ("ws6", "factory",         200f,  250f),
        };
        foreach (var (id, label, gx, gy) in ghosts)
        {
            g.Nodes.Add(new BrainNode
            {
                Id = id, Type = BrainNodeType.Workspace, Label = label,
                BaseX = gx, BaseY = gy, IsGhost = true,
            });
            g.Edges.Add(new BrainEdge { FromId = "hermes-cs", ToId = id, Strength = 0.1f });
        }
        g.Edges.Add(new BrainEdge { FromId = "ws4", ToId = "hermes-cs", Strength = 0.15f });
        g.Edges.Add(new BrainEdge { FromId = "ws1", ToId = "ws2", Strength = 0.05f });
        g.Edges.Add(new BrainEdge { FromId = "ws3", ToId = "ws4", Strength = 0.08f });
        g.Edges.Add(new BrainEdge { FromId = "ws5", ToId = "ws6", Strength = 0.05f });
        g.Edges.Add(new BrainEdge { FromId = "ws2", ToId = "ws5", Strength = 0.06f });

        // Recent files (close to center)
        var files = new[]
        {
            ("f1", "App.xaml",         -1.8f, 80f),
            ("f2", "Dashboard.xaml",   -2.2f, 90f),
            ("f3", "MainWindow.xaml",  -1.4f, 85f),
            ("f4", "ChatPage.xaml",    -2.5f, 75f),
        };
        foreach (var (id, label, angle, r) in files)
        {
            g.Nodes.Add(new BrainNode
            {
                Id = id, Type = BrainNodeType.File, Label = label,
                BaseX = cx + MathF.Cos(angle) * r,
                BaseY = cy + MathF.Sin(angle) * r,
            });
            g.Edges.Add(new BrainEdge { FromId = "hermes-cs", ToId = id, Strength = 0.2f });
        }
        g.Edges.Add(new BrainEdge { FromId = "s1", ToId = "f1", Strength = 0.3f });
        g.Edges.Add(new BrainEdge { FromId = "s1", ToId = "f2", Strength = 0.4f });

        return g;
    }
}
