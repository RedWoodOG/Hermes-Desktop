namespace Hermes.Agent.Soul;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// A recorded mistake — when the agent did something wrong and the user corrected it.
/// Append-only journal stored as JSONL.
/// </summary>
public sealed record MistakeEntry
{
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>What the agent was trying to do when the mistake happened.</summary>
    [JsonPropertyName("context")]
    public required string Context { get; init; }

    /// <summary>What went wrong — the incorrect action or output.</summary>
    [JsonPropertyName("mistake")]
    public required string Mistake { get; init; }

    /// <summary>What the user said or did to correct it.</summary>
    [JsonPropertyName("correction")]
    public required string Correction { get; init; }

    /// <summary>The distilled lesson — what to do differently next time.</summary>
    [JsonPropertyName("lesson")]
    public required string Lesson { get; init; }
}

/// <summary>
/// A recorded good habit — when the agent did something right and the user confirmed it.
/// Append-only journal stored as JSONL.
/// </summary>
public sealed record HabitEntry
{
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>What the agent was doing when the positive feedback occurred.</summary>
    [JsonPropertyName("context")]
    public required string Context { get; init; }

    /// <summary>The good practice or approach that worked well.</summary>
    [JsonPropertyName("habit")]
    public required string Habit { get; init; }

    /// <summary>The user's positive feedback or confirmation.</summary>
    [JsonPropertyName("positiveFeedback")]
    public required string PositiveFeedback { get; init; }
}

/// <summary>
/// Result of soul extraction from a transcript batch.
/// </summary>
public sealed class SoulExtractionResult
{
    public List<MistakeEntry> Mistakes { get; init; } = new List<MistakeEntry>();
    public List<HabitEntry> Habits { get; init; } = new List<HabitEntry>();

    /// <summary>
    /// Signals about the user's profile extracted from the conversation
    /// (expertise level, preferences, communication style).
    /// Null if no significant signals detected.
    /// </summary>
    public string? UserProfileUpdate { get; init; }
}

/// <summary>
/// Types of soul files.
/// </summary>
public enum SoulFileType
{
    /// <summary>Agent identity — who the agent is (SOUL.md).</summary>
    Soul,

    /// <summary>User profile — who the user is (USER.md).</summary>
    User,

    /// <summary>Project rules — conventions for the current project (AGENTS.md).</summary>
    ProjectRules
}
