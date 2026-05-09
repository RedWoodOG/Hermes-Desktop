namespace Hermes.Agent.Core;

/// <summary>
/// Typed chat runtime events that separate agent execution from UI rendering.
/// Adapted from DeepSeek-TUI's engine-event boundary for Hermes desktop.
/// </summary>
public abstract record ChatRuntimeEvent
{
    public sealed record TokenDelta(string Text) : ChatRuntimeEvent;
    public sealed record ThinkingDelta(string Text) : ChatRuntimeEvent;
    public sealed record ToolStatus(string Text) : ChatRuntimeEvent;
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
