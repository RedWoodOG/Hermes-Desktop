using System.Threading;

namespace Hermes.Agent.Core;

public enum ActivityStatus { Running, Success, Failed, Denied }

public sealed class ActivityEntry
{
    // Process-wide monotonically increasing counter used as a stable secondary
    // sort key when two ActivityEntry instances share the same Timestamp.
    // DateTime.UtcNow only has ~16ms tick resolution on Windows and parallel
    // tool execution can produce two entries in the same tick, so sorting by
    // Timestamp alone is not deterministic — Sequence guarantees insertion
    // order is preserved when ReplayPanel sorts for chronological playback.
    private static long _nextSequence;

    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Process-monotonic creation sequence number. Used by ReplayPanel as a
    /// tie-breaker when sorting by Timestamp for playback so two activities
    /// created in the same tick still play back in the order they were
    /// recorded. Assigned automatically; do not set explicitly.
    /// </summary>
    public long Sequence { get; init; } = Interlocked.Increment(ref _nextSequence);

    public long DurationMs { get; set; }
    public required string ToolName { get; init; }
    public string? ToolCallId { get; init; }
    public string InputSummary { get; set; } = "";
    public string OutputSummary { get; set; } = "";
    public ActivityStatus Status { get; set; } = ActivityStatus.Running;
    public string? DiffPreview { get; set; }
    public string? ScreenshotPath { get; set; }
}
