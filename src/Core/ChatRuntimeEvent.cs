namespace Hermes.Agent.Core;

using System.Text.Json;
using Hermes.Agent.LLM;

/// <summary>
/// Typed chat runtime events that separate agent execution from UI rendering.
/// Adapted from DeepSeek-TUI's engine-event boundary for Hermes desktop.
/// </summary>
public abstract record ChatRuntimeEvent
{
    public sealed record TokenDelta(string Text) : ChatRuntimeEvent;
    public sealed record ThinkingDelta(string Text) : ChatRuntimeEvent;
    public sealed record ToolStatus(string Text) : ChatRuntimeEvent;

    /// <summary>Structured tool-use start signal (id + name). Use this instead of <see cref="ToolStatus"/> when the underlying stream emits structured tool envelopes.</summary>
    public sealed record ToolUseStart(string Id, string Name) : ChatRuntimeEvent;

    /// <summary>Partial JSON fragment of a tool call's arguments — same call <paramref name="Id"/> as the preceding <see cref="ToolUseStart"/>.</summary>
    public sealed record ToolUseDelta(string Id, string PartialJson) : ChatRuntimeEvent;

    /// <summary>Tool call arguments fully assembled. Consumers can deserialize <paramref name="Arguments"/> for inspection.</summary>
    public sealed record ToolUseComplete(string Id, string Name, JsonElement Arguments) : ChatRuntimeEvent;

    /// <summary>Final per-turn token usage. Wired into the Bundle E.3 usage footer and <c>/usage</c> slash command.</summary>
    public sealed record Usage(UsageStats Stats, string? StopReason = null) : ChatRuntimeEvent;

    public sealed record Error(ChatRuntimeError Detail) : ChatRuntimeEvent;
    public sealed record Completed(string SessionId) : ChatRuntimeEvent;
}

public sealed record ChatRuntimeError(
    string Message,
    string Code = "stream_error",
    bool Retryable = true,
    string? SuggestedAction = null,
    string Severity = "error");

/// <summary>
/// Typed commands the UI can send to the chat runtime.
/// This is intentionally small today; it gives the desktop app a stable seam
/// for cancel/retry/steer work without screen or control coupling.
/// </summary>
public abstract record ChatRuntimeCommand
{
    public sealed record SendMessage(string Text) : ChatRuntimeCommand;
    public sealed record CancelTurn : ChatRuntimeCommand;
    public sealed record RetryLast : ChatRuntimeCommand;
    public sealed record SwitchModel(string Provider, string Model) : ChatRuntimeCommand;
}
