using System;
using System.Collections.Generic;
using System.Linq;
using HermesDesktop.Models;
using Hermes.Agent.Core;

namespace HermesDesktop.Services;

/// <summary>
/// Builds a per-journey <see cref="BrainGraphData"/> from that journey's
/// ActivityEntry list -- the mini-constellation that sits in the chat's
/// right rail. Same node-type palette as the dashboard's BrainGraphData,
/// scoped to just the tools/memories/skills/files this journey touched.
/// </summary>
public sealed class JourneyGraphService
{
    private const int MaxPerCategory = 6;

    public BrainGraphData Build(string journeyTitle, IReadOnlyList<ActivityEntry> entries)
    {
        var graph = new BrainGraphData();

        var centerLabel = string.IsNullOrWhiteSpace(journeyTitle) ? "Journey" : journeyTitle;
        if (centerLabel.Length > 22) centerLabel = centerLabel[..20] + "\u2026";

        graph.Nodes.Add(new BrainNode
        {
            Id = "journey-center",
            Type = BrainNodeType.Project,
            Label = centerLabel,
            Sublabel = entries.Count == 0 ? "" : $"{entries.Count} events",
            BaseX = 0, BaseY = 0,
        });

        if (entries.Count == 0) return graph;

        // Dedupe per category (preserve most-recent first).
        var tools    = new List<string>();
        var memories = new List<string>();
        var skills   = new List<string>();
        var files    = new List<string>();
        var seen     = new HashSet<string>(StringComparer.Ordinal);

        foreach (var e in entries.Reverse())
        {
            var (kind, label) = Classify(e);
            var key = $"{(int)kind}|{label}";
            if (!seen.Add(key)) continue;
            switch (kind)
            {
                case BrainNodeType.Memory when memories.Count < MaxPerCategory: memories.Add(label); break;
                case BrainNodeType.Skill  when skills.Count   < MaxPerCategory: skills.Add(label);   break;
                case BrainNodeType.File   when files.Count    < MaxPerCategory: files.Add(label);    break;
                case BrainNodeType.Tool   when tools.Count    < MaxPerCategory: tools.Add(label);    break;
            }
        }

        // Tools: bottom-left arc
        for (int i = 0; i < tools.Count; i++)
        {
            var angle = MathF.PI - 0.5f + i * 0.32f;
            var r = 78f + (i % 2) * 10f;
            var id = $"tool-{tools[i]}";
            graph.Nodes.Add(new BrainNode
            {
                Id = id,
                Type = BrainNodeType.Tool,
                Label = tools[i],
                BaseX = MathF.Cos(angle) * r,
                BaseY = MathF.Sin(angle) * r,
            });
            graph.Edges.Add(new BrainEdge { FromId = "journey-center", ToId = id, Strength = 0.5f });
        }

        // Memories: right arc
        for (int i = 0; i < memories.Count; i++)
        {
            var angle = 0.3f + (i - memories.Count / 2f) * 0.4f;
            var r = 78f + (i % 2) * 8f;
            var id = $"memory-{memories[i]}";
            graph.Nodes.Add(new BrainNode
            {
                Id = id,
                Type = BrainNodeType.Memory,
                Label = memories[i],
                BaseX = MathF.Cos(angle) * r,
                BaseY = MathF.Sin(angle) * r,
            });
            graph.Edges.Add(new BrainEdge { FromId = "journey-center", ToId = id, Strength = 0.4f });
        }

        // Skills: top-right arc
        for (int i = 0; i < skills.Count; i++)
        {
            var angle = -0.8f - i * 0.3f;
            var r = 82f;
            var id = $"skill-{skills[i]}";
            graph.Nodes.Add(new BrainNode
            {
                Id = id,
                Type = BrainNodeType.Skill,
                Label = skills[i],
                BaseX = MathF.Cos(angle) * r,
                BaseY = MathF.Sin(angle) * r,
            });
            graph.Edges.Add(new BrainEdge { FromId = "journey-center", ToId = id, Strength = 0.5f });
        }

        // Files: bottom arc
        for (int i = 0; i < files.Count; i++)
        {
            var angle = MathF.PI / 2 + 0.15f + i * 0.28f;
            var r = 72f;
            var id = $"file-{files[i]}";
            graph.Nodes.Add(new BrainNode
            {
                Id = id,
                Type = BrainNodeType.File,
                Label = files[i],
                BaseX = MathF.Cos(angle) * r,
                BaseY = MathF.Sin(angle) * r,
            });
            graph.Edges.Add(new BrainEdge { FromId = "journey-center", ToId = id, Strength = 0.3f });
        }

        return graph;
    }

    // ── Classification ──

    private static (BrainNodeType kind, string label) Classify(ActivityEntry e)
    {
        var toolName = e.ToolName ?? "";
        var lower = toolName.ToLowerInvariant();
        var summary = e.InputSummary ?? "";

        if (lower.StartsWith("memory", StringComparison.Ordinal))
            return (BrainNodeType.Memory, ShortLabel(summary, toolName, max: 14));
        if (lower.StartsWith("skill", StringComparison.Ordinal))
            return (BrainNodeType.Skill, ShortLabel(summary, toolName, max: 14));
        if (lower is "read_file" or "write_file" or "edit_file" or "glob" or "grep"
                  or "patch" or "readfile" or "writefile" or "editfile"
                  or "ls" or "list_directory")
            return (BrainNodeType.File, ExtractFileName(summary, toolName));
        return (BrainNodeType.Tool, toolName);
    }

    private static string ShortLabel(string summary, string fallback, int max)
    {
        var s = string.IsNullOrWhiteSpace(summary) ? fallback : summary;
        return s.Length > max ? s[..Math.Max(1, max - 1)] + "\u2026" : s;
    }

    // Pull the first path-looking token out of an InputSummary and return just
    // its file name (or a truncated fallback). Best-effort; brittle inputs
    // just fall back to the tool name.
    private static string ExtractFileName(string summary, string fallback)
    {
        if (string.IsNullOrWhiteSpace(summary)) return fallback;
        var tokens = summary.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in tokens)
        {
            var t = raw.Trim('"', '\'', ',', '(', ')', '[', ']');
            if (t.Contains('/') || t.Contains('\\'))
            {
                var lastSep = t.LastIndexOfAny(new[] { '/', '\\' });
                var name = (lastSep >= 0 && lastSep < t.Length - 1) ? t[(lastSep + 1)..] : t;
                if (name.Length > 18) name = name[..16] + "\u2026";
                return name.Length == 0 ? fallback : name;
            }
        }
        return ShortLabel(summary, fallback, max: 18);
    }
}
