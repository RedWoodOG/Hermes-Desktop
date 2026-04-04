namespace Hermes.Agent.Context;

/// <summary>
/// Structured rolling memory that replaces raw transcript as "what the model knows."
/// Compact, structured, and stable — doesn't change wildly every turn so provider caching works.
/// </summary>
public sealed class SessionState
{
    /// <summary>The current task or objective the conversation is working toward.</summary>
    public string ActiveGoal { get; set; } = string.Empty;

    /// <summary>Rules, boundaries, or requirements that constrain how the goal is pursued.</summary>
    public List<string> Constraints { get; set; } = new();

    /// <summary>Key decisions made during the conversation, with rationale.</summary>
    public List<Decision> Decisions { get; set; } = new();

    /// <summary>Unresolved items that still need answers or investigation.</summary>
    public List<string> OpenQuestions { get; set; } = new();

    /// <summary>Key names, file paths, or domain terms referenced in the conversation.</summary>
    public List<string> ImportantEntities { get; set; } = new();

    /// <summary>Compressed summary of older turns evicted from the recent window.</summary>
    public SessionSummary Summary { get; set; } = new();

    /// <summary>
    /// OpenAI response_id for cache chaining across turns.
    /// When set, subsequent requests can reference this to reuse cached KV computations.
    /// </summary>
    public string? PreviousResponseId { get; set; }

    /// <summary>
    /// Monotonically increasing turn counter for staleness detection.
    /// </summary>
    public int TurnCount { get; set; }

    /// <summary>
    /// Estimates the token footprint of this session state using a simple heuristic:
    /// ~4 characters per token (conservative for English text + JSON overhead).
    /// </summary>
    public int EstimateTokens()
    {
        var chars = 0;

        chars += ActiveGoal.Length;

        foreach (var c in Constraints)
            chars += c.Length;

        foreach (var d in Decisions)
            chars += d.What.Length + d.Why.Length;

        foreach (var q in OpenQuestions)
            chars += q.Length;

        foreach (var e in ImportantEntities)
            chars += e.Length;

        chars += Summary.Content.Length;

        // ~4 chars per token, plus JSON structure overhead (~20%)
        return (int)((chars / 4.0) * 1.2);
    }

    /// <summary>
    /// Trims the oldest decisions and resolved questions when state grows too large.
    /// Keeps the most recent items which are most relevant to current work.
    /// </summary>
    public void Compact(int maxDecisions = 10, int maxQuestions = 5, int maxEntities = 20)
    {
        if (Decisions.Count > maxDecisions)
            Decisions = Decisions.GetRange(Decisions.Count - maxDecisions, maxDecisions);

        if (OpenQuestions.Count > maxQuestions)
            OpenQuestions = OpenQuestions.GetRange(OpenQuestions.Count - maxQuestions, maxQuestions);

        if (ImportantEntities.Count > maxEntities)
            ImportantEntities = ImportantEntities.GetRange(ImportantEntities.Count - maxEntities, maxEntities);
    }
}

/// <summary>A recorded decision with rationale, anchored to a specific turn.</summary>
public sealed class Decision
{
    /// <summary>What was decided.</summary>
    public required string What { get; init; }

    /// <summary>Why this choice was made.</summary>
    public required string Why { get; init; }

    /// <summary>The turn number when this decision was recorded.</summary>
    public int TurnNumber { get; init; }
}

/// <summary>Rolling summary of conversation turns that have been evicted from the recent window.</summary>
public sealed class SessionSummary
{
    /// <summary>
    /// Compressed representation of older turns that have been evicted from the recent window.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// The turn number up to which this summary covers.
    /// Turns after this number are still in the recent window.
    /// </summary>
    public int CoveredThroughTurn { get; set; }
}
