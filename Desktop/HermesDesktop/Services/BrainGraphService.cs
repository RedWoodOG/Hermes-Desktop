using HermesDesktop.Models;
using Hermes.Agent.Core;
using Hermes.Agent.Skills;
using Hermes.Agent.Memory;
using Hermes.Agent.Transcript;
using Hermes.Agent.Analytics;

namespace HermesDesktop.Services;

/// <summary>
/// Aggregates data from multiple backend services into a <see cref="BrainGraphData"/>
/// for the constellation visualization.
/// </summary>
public sealed class BrainGraphService
{
    private readonly IServiceProvider _services;

    public BrainGraphService(IServiceProvider services) => _services = services;

    public async Task<BrainGraphData> BuildGraphAsync(CancellationToken ct)
    {
        var graph = new BrainGraphData();

        var projectName = Path.GetFileName(HermesEnvironment.AgentWorkingDirectory);
        var version = typeof(BrainGraphService).Assembly.GetName().Version;
        var versionStr = version is not null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "";

        // ── Center: current project ──
        graph.Nodes.Add(new BrainNode
        {
            Id = "project-center",
            Type = BrainNodeType.Project,
            Label = projectName,
            Sublabel = $"{versionStr} · ONLINE",
            BaseX = 0, BaseY = 0,
        });

        // ── Sessions ──
        BuildSessionNodes(graph);

        // ── Tools ──
        BuildToolNodes(graph);

        // ── Skills ──
        BuildSkillNodes(graph);

        // ── Memories ──
        await BuildMemoryNodesAsync(graph, ct);

        // ── Files (recent from project) ──
        BuildFileNodes(graph);

        // ── Ghost workspaces ──
        BuildGhostWorkspaces(graph);

        // ── Cross-links ──
        BuildCrossLinks(graph);

        return graph;
    }

    private void BuildSessionNodes(BrainGraphData graph)
    {
        var transcripts = _services.GetService(typeof(TranscriptStore)) as TranscriptStore;
        if (transcripts is null) return;

        var ids = transcripts.GetAllSessionIds();
        var recent = ids.TakeLast(10).Reverse().ToList();

        for (int i = 0; i < recent.Count; i++)
        {
            var angle = -MathF.PI / 2 + (i - recent.Count / 2f) * 0.6f;
            var radius = 140f + (i % 2) * 20f;
            var id = recent[i];
            var shortId = id.Length > 12 ? id[..12] : id;

            graph.Nodes.Add(new BrainNode
            {
                Id = $"session-{id}",
                Type = BrainNodeType.Session,
                Label = $"Session {i + 1}",
                Sublabel = shortId,
                BaseX = MathF.Cos(angle) * radius,
                BaseY = MathF.Sin(angle) * radius,
            });
            graph.Edges.Add(new BrainEdge
            {
                FromId = "project-center",
                ToId = $"session-{id}",
                Strength = i == 0 ? 0.8f : 0.5f,
            });
        }
    }

    private void BuildToolNodes(BrainGraphData graph)
    {
        var agent = _services.GetService(typeof(Agent)) as Agent;
        if (agent is null) return;

        var tools = agent.Tools.Keys.Take(20).ToList();
        for (int i = 0; i < tools.Count; i++)
        {
            var angle = MathF.PI - 0.8f + i * 0.28f;
            var radius = 140f + (i % 3) * 30f;

            graph.Nodes.Add(new BrainNode
            {
                Id = $"tool-{tools[i]}",
                Type = BrainNodeType.Tool,
                Label = tools[i],
                BaseX = MathF.Cos(angle) * radius,
                BaseY = MathF.Sin(angle) * radius,
            });
            graph.Edges.Add(new BrainEdge
            {
                FromId = "project-center",
                ToId = $"tool-{tools[i]}",
                Strength = 0.3f,
            });
        }
    }

    private void BuildSkillNodes(BrainGraphData graph)
    {
        var skillManager = _services.GetService(typeof(SkillManager)) as SkillManager;
        if (skillManager is null) return;

        var skills = skillManager.ListSkills();
        for (int i = 0; i < skills.Count; i++)
        {
            var angle = MathF.PI / 2 + 0.5f + i * 0.45f;
            var radius = 155f;

            graph.Nodes.Add(new BrainNode
            {
                Id = $"skill-{skills[i].Name}",
                Type = BrainNodeType.Skill,
                Label = skills[i].Name,
                BaseX = MathF.Cos(angle) * radius,
                BaseY = MathF.Sin(angle) * radius,
            });
            graph.Edges.Add(new BrainEdge
            {
                FromId = "project-center",
                ToId = $"skill-{skills[i].Name}",
                Strength = 0.5f,
            });
        }
    }

    private async Task BuildMemoryNodesAsync(BrainGraphData graph, CancellationToken ct)
    {
        var memoryManager = _services.GetService(typeof(MemoryManager)) as MemoryManager;
        if (memoryManager is null) return;

        try
        {
            var memories = await memoryManager.LoadAllMemoriesAsync(ct);
            var items = memories.Take(20).ToList();

            for (int i = 0; i < items.Count; i++)
            {
                var angle = 0.3f + (i - items.Count / 2f) * 0.4f;
                var radius = 160f + (i % 2) * 40f;

                graph.Nodes.Add(new BrainNode
                {
                    Id = $"memory-{i}",
                    Type = BrainNodeType.Memory,
                    Label = Path.GetFileNameWithoutExtension(items[i].Filename),
                    BaseX = MathF.Cos(angle) * radius,
                    BaseY = MathF.Sin(angle) * radius,
                });
                graph.Edges.Add(new BrainEdge
                {
                    FromId = "project-center",
                    ToId = $"memory-{i}",
                    Strength = 0.4f,
                });
            }

            // Inter-memory links
            for (int i = 0; i < items.Count - 1; i += 2)
            {
                graph.Edges.Add(new BrainEdge
                {
                    FromId = $"memory-{i}",
                    ToId = $"memory-{i + 1}",
                    Strength = 0.15f,
                });
            }
        }
        catch
        {
            // Memory scan is best-effort
        }
    }

    private static readonly HashSet<string> ExcludedFileScanDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "packages", "TestResults", "dist", "out",
    };

    private static readonly HashSet<string> FileScanExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".csproj", ".sln", ".md", ".json", ".yml", ".yaml", ".ps1",
    };

    private void BuildFileNodes(BrainGraphData graph)
    {
        var projectDir = HermesEnvironment.AgentWorkingDirectory;
        if (string.IsNullOrEmpty(projectDir) || !Directory.Exists(projectDir)) return;

        List<FileInfo> recent;
        try
        {
            recent = EnumerateSourceFiles(projectDir)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(6)
                .ToList();
        }
        catch
        {
            return;
        }

        for (int i = 0; i < recent.Count; i++)
        {
            var file = recent[i];
            var angle = -1.8f - i * 0.25f;
            var radius = 75f + (i % 2) * 15f;

            graph.Nodes.Add(new BrainNode
            {
                Id = $"file-{file.FullName}",
                Type = BrainNodeType.File,
                Label = file.Name,
                BaseX = MathF.Cos(angle) * radius,
                BaseY = MathF.Sin(angle) * radius,
            });
            graph.Edges.Add(new BrainEdge
            {
                FromId = "project-center",
                ToId = $"file-{file.FullName}",
                Strength = 0.2f,
            });
        }
    }

    private static IEnumerable<FileInfo> EnumerateSourceFiles(string root)
    {
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(root));
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            FileInfo[] files;
            DirectoryInfo[] subdirs;
            try
            {
                files = dir.GetFiles();
                subdirs = dir.GetDirectories();
            }
            catch
            {
                continue;
            }

            foreach (var f in files)
                if (FileScanExtensions.Contains(f.Extension))
                    yield return f;

            foreach (var sub in subdirs)
                if (!sub.Name.StartsWith('.') && !ExcludedFileScanDirs.Contains(sub.Name))
                    stack.Push(sub);
        }
    }

    private void BuildGhostWorkspaces(BrainGraphData graph)
    {
        // Discover sibling directories in the workspace parent
        var workDir = HermesEnvironment.AgentWorkingDirectory;
        var parentDir = Path.GetDirectoryName(workDir);
        if (parentDir is null || !Directory.Exists(parentDir)) return;

        try
        {
            var siblings = Directory.GetDirectories(parentDir)
                .Where(d => d != workDir)
                .Select(d => Path.GetFileName(d))
                .Where(n => !string.IsNullOrEmpty(n) && !n.StartsWith('.'))
                .Take(8)
                .ToList();

            var ghostPositions = new (float x, float y)[]
            {
                (-380, -180), (-340, 140), (350, -160), (380, 120),
                (-100, 260), (200, 250), (-250, -250), (300, -250),
            };

            for (int i = 0; i < siblings.Count && i < ghostPositions.Length; i++)
            {
                var (x, y) = ghostPositions[i];
                graph.Nodes.Add(new BrainNode
                {
                    Id = $"ws-{siblings[i]}",
                    Type = BrainNodeType.Workspace,
                    Label = siblings[i]!,
                    BaseX = x,
                    BaseY = y,
                    IsGhost = true,
                });
                graph.Edges.Add(new BrainEdge
                {
                    FromId = "project-center",
                    ToId = $"ws-{siblings[i]}",
                    Strength = 0.1f,
                });
            }

            // Faint inter-ghost links for visual web
            for (int i = 0; i < siblings.Count - 1; i += 2)
            {
                graph.Edges.Add(new BrainEdge
                {
                    FromId = $"ws-{siblings[i]}",
                    ToId = $"ws-{siblings[i + 1]}",
                    Strength = 0.05f,
                });
            }
        }
        catch
        {
            // Ghost workspace discovery is best-effort
        }
    }

    private void BuildCrossLinks(BrainGraphData graph)
    {
        // Session-to-tool links from insights (most used tools)
        var insights = _services.GetService(typeof(InsightsService)) as InsightsService;
        if (insights is null) return;

        var data = insights.GetInsights();
        var topTools = data.ToolUsage
            .OrderByDescending(kv => kv.Value.TotalCalls)
            .Take(5)
            .Select(kv => kv.Key)
            .ToList();

        // Link the first session to the top tools
        var firstSession = graph.Nodes.FirstOrDefault(n => n.Type == BrainNodeType.Session);
        if (firstSession is null) return;

        foreach (var toolName in topTools)
        {
            var toolNode = graph.GetNode($"tool-{toolName}");
            if (toolNode is not null)
            {
                graph.Edges.Add(new BrainEdge
                {
                    FromId = firstSession.Id,
                    ToId = toolNode.Id,
                    Strength = 0.4f,
                });
            }
        }
    }
}
