namespace HermesDesktop.Models;

/// <summary>
/// Node types in the brain graph constellation.
/// </summary>
public enum BrainNodeType
{
    Project,
    Session,
    Memory,
    Tool,
    Skill,
    Workspace,
    File,
}

/// <summary>
/// A single node in the brain graph.
/// </summary>
public sealed class BrainNode
{
    public required string Id { get; init; }
    public required BrainNodeType Type { get; init; }
    public required string Label { get; init; }
    public string? Sublabel { get; init; }

    /// <summary>Layout position relative to center (0,0).</summary>
    public float BaseX { get; set; }
    /// <summary>Layout position relative to center (0,0).</summary>
    public float BaseY { get; set; }

    /// <summary>Animated position (updated per frame by the renderer).</summary>
    public float X { get; set; }
    /// <summary>Animated position (updated per frame by the renderer).</summary>
    public float Y { get; set; }

    /// <summary>Ghost nodes are dimmed workspace nodes at the periphery.</summary>
    public bool IsGhost { get; init; }

    /// <summary>Diameter in logical pixels, determined by node type.</summary>
    public float Size => Type switch
    {
        BrainNodeType.Project   => 28f,
        BrainNodeType.Workspace => 18f,
        BrainNodeType.Session   => 14f,
        BrainNodeType.Skill     => 10f,
        BrainNodeType.Memory    => 10f,
        BrainNodeType.Tool      => 8f,
        BrainNodeType.File      => 6f,
        _ => 8f,
    };
}

/// <summary>
/// A connection between two brain graph nodes.
/// </summary>
public sealed class BrainEdge
{
    public required string FromId { get; init; }
    public required string ToId { get; init; }

    /// <summary>Connection strength 0.0–1.0. Controls line opacity and particle visibility.</summary>
    public float Strength { get; init; } = 0.3f;
}

/// <summary>
/// Complete brain graph data — nodes and edges for the constellation visualization.
/// </summary>
public sealed class BrainGraphData
{
    public List<BrainNode> Nodes { get; } = new();
    public List<BrainEdge> Edges { get; } = new();

    public BrainNode? GetNode(string id) => Nodes.FirstOrDefault(n => n.Id == id);
}
