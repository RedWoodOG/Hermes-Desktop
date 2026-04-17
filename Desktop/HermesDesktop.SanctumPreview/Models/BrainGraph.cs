namespace SanctumPreview.Models;

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

public sealed class BrainNode
{
    public required string Id { get; init; }
    public required BrainNodeType Type { get; init; }
    public required string Label { get; init; }
    public string? Sublabel { get; init; }

    public float BaseX { get; set; }
    public float BaseY { get; set; }

    public float X { get; set; }
    public float Y { get; set; }

    public bool IsGhost { get; init; }

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

public sealed class BrainEdge
{
    public required string FromId { get; init; }
    public required string ToId { get; init; }
    public float Strength { get; init; } = 0.3f;
}

public sealed class BrainGraphData
{
    public List<BrainNode> Nodes { get; } = new();
    public List<BrainEdge> Edges { get; } = new();

    public BrainNode? GetNode(string id) => Nodes.FirstOrDefault(n => n.Id == id);
}
