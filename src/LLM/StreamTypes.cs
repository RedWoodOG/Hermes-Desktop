namespace Hermes.Agent.LLM;

using System.Text.Json;

/// <summary>
/// Events emitted during streaming completion.
/// </summary>
public abstract record StreamEvent
{
    /// <summary>
    /// Text token received.
    /// </summary>
    public sealed record TokenDelta(string Text) : StreamEvent;
    
    /// <summary>
    /// Tool use started.
    /// </summary>
    public sealed record ToolUseStart(string Id, string Name) : StreamEvent;
    
    /// <summary>
    /// Partial tool use JSON received.
    /// </summary>
    public sealed record ToolUseDelta(string Id, string PartialJson) : StreamEvent;
    
    /// <summary>
    /// Tool use completed (full JSON available).
    /// </summary>
    public sealed record ToolUseComplete(string Id, string Name, JsonElement Arguments) : StreamEvent;
    
    /// <summary>
    /// Thinking/reasoning content (for extended thinking models).
    /// </summary>
    public sealed record ThinkingDelta(string Text) : StreamEvent;
    
    /// <summary>
    /// Message completed with stop reason.
    /// </summary>
    public sealed record MessageComplete(string StopReason, UsageStats? Usage = null) : StreamEvent;
    
    /// <summary>
    /// Error occurred during streaming.
    /// </summary>
    public sealed record StreamError(Exception Error, ProviderErrorCode Code = ProviderErrorCode.Unknown) : StreamEvent;
}

/// <summary>
/// Token usage statistics.
/// </summary>
public sealed record UsageStats(
    int InputTokens,
    int OutputTokens,
    int? CacheCreationTokens = null,
    int? CacheReadTokens = null);

/// <summary>
/// Complete message from streaming response.
/// </summary>
public sealed class StreamedMessage
{
    public string? Content { get; set; }
    public string? StopReason { get; set; }
    public List<ToolUseBlock> ToolUses { get; } = new();
    public UsageStats? Usage { get; set; }
    
    public bool HasToolUse => ToolUses.Count > 0;
}

/// <summary>
/// Tool use block in a message.
/// </summary>
public sealed record ToolUseBlock(
    string Id,
    string Name,
    JsonElement Arguments);
