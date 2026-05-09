namespace Hermes.Agent.Transcript;

public sealed record ThreadRecord
{
    public required int SchemaVersion { get; init; }
    public required string ThreadId { get; init; }
    public required string SessionId { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
    public string Platform { get; init; } = "desktop";
    public string? WorkspaceRoot { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? Title { get; init; }
    public bool Archived { get; init; }
    public string? ParentThreadId { get; init; }
    public string? LatestTurnId { get; init; }
}

public sealed record TurnRecord
{
    public required int SchemaVersion { get; init; }
    public required string TurnId { get; init; }
    public required string ThreadId { get; init; }
    public required int Sequence { get; init; }
    public required TurnStatus Status { get; init; }
    public required DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public long? DurationMs { get; init; }
    public required string InputSummary { get; init; }
    public string? Error { get; init; }
    public IReadOnlyDictionary<string, long> Usage { get; init; } = new Dictionary<string, long>();
    public IReadOnlyList<string> ItemIds { get; init; } = Array.Empty<string>();
}

public sealed record TurnItemRecord
{
    public required int SchemaVersion { get; init; }
    public required string ItemId { get; init; }
    public required string TurnId { get; init; }
    public required string ThreadId { get; init; }
    public required int Sequence { get; init; }
    public required TurnItemKind Kind { get; init; }
    public string? Role { get; init; }
    public required string ContentSummary { get; init; }
    public int? MessageIndex { get; init; }
    public string? MessageRef { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public required TurnItemStatus Status { get; init; }
    public string? ArtifactPath { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    public required DateTime CreatedAt { get; init; }
}

public sealed record RuntimeEventRecord
{
    public required int SchemaVersion { get; init; }
    public required long EventSequence { get; init; }
    public required string ThreadId { get; init; }
    public string? TurnId { get; init; }
    public string? ItemId { get; init; }
    public required string EventType { get; init; }
    public required DateTime Timestamp { get; init; }
    public IReadOnlyDictionary<string, string> Payload { get; init; } = new Dictionary<string, string>();
}

public enum TurnStatus
{
    Queued,
    InProgress,
    Completed,
    Failed,
    Interrupted,
    Canceled
}

public enum TurnItemKind
{
    UserMessage,
    AssistantMessage,
    AgentReasoning,
    ToolCall,
    ToolResult,
    Status,
    Error
}

public enum TurnItemStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Canceled
}
