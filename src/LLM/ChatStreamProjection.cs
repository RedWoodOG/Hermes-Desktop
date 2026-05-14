namespace Hermes.Agent.LLM;

using System;

/// <summary>
/// Categories surfaced by <see cref="ChatStreamProjection.Project"/>.
/// </summary>
public enum ChatStreamEventKind
{
    /// <summary>Assistant content token that should accumulate into the saved response.</summary>
    Token,

    /// <summary>Reasoning / thinking content (extended-thinking models) — not persisted as response text.</summary>
    Thinking,

    /// <summary>Tool invocation has started. <see cref="ChatStreamEnvelope.ToolName"/> is set.</summary>
    ToolStart,

    /// <summary>Partial tool arguments JSON. <see cref="ChatStreamEnvelope.ToolCallId"/> is set.</summary>
    ToolDelta,

    /// <summary>Tool arguments fully assembled. <see cref="ChatStreamEnvelope.Text"/> holds the JSON.</summary>
    ToolComplete,

    /// <summary>Final usage stats for the turn. <see cref="ChatStreamEnvelope.Usage"/> is set.</summary>
    Usage,

    /// <summary>Stream error. <see cref="ChatStreamEnvelope.Text"/> holds the message.</summary>
    Error,
}

/// <summary>
/// UI-facing projection of <see cref="StreamEvent"/>.
/// <para><c>Text</c> carries different content per <c>Kind</c>:</para>
/// <list type="bullet">
///   <item>Token / Thinking — the streamed text fragment.</item>
///   <item>ToolStart — empty; tool name is in <c>ToolName</c>.</item>
///   <item>ToolDelta — the partial JSON fragment.</item>
///   <item>ToolComplete — the full arguments JSON.</item>
///   <item>Usage — the stop reason; token counts are in <c>Usage</c>.</item>
///   <item>Error — the error message.</item>
/// </list>
/// </summary>
public sealed record ChatStreamEnvelope(
    ChatStreamEventKind Kind,
    string Text,
    string? ToolName = null,
    string? ToolCallId = null,
    UsageStats? Usage = null);

/// <summary>
/// Pure projection result from <see cref="ChatStreamProjection.Project"/>.
/// <para><c>Envelope</c> is <c>null</c> when the upstream event has no UI mapping
/// (for example a <c>MessageComplete</c> without usage stats).</para>
/// <para><c>AccumulatedText</c> is the text that the caller should append to the persisted
/// assistant response — <c>null</c> for non-content events.</para>
/// </summary>
public readonly record struct ChatStreamProjectionResult(
    ChatStreamEnvelope? Envelope,
    string? AccumulatedText);

/// <summary>
/// Projects core <see cref="StreamEvent"/> values into UI-friendly <see cref="ChatStreamEnvelope"/>s.
/// Pure: no I/O, no state, fully testable.
/// </summary>
public static class ChatStreamProjection
{
    /// <summary>
    /// Project a single <paramref name="evt"/> into a UI envelope plus optional accumulated text.
    /// Returns a result with <c>Envelope = null</c> when the event has no UI mapping (currently
    /// only <c>MessageComplete</c> without usage stats).
    /// </summary>
    public static ChatStreamProjectionResult Project(StreamEvent evt) => evt switch
    {
        // One-release fallback for the legacy "\n[Calling tool: X]\n" TokenDelta emitted by
        // src/Core/Agent.cs while Core still synthesizes it. Once Core stops emitting that
        // cosmetic marker, this branch can be deleted in favor of the structured ToolUseStart.
        StreamEvent.TokenDelta td when
            td.Text.StartsWith("\n[Calling tool:", StringComparison.Ordinal) &&
            td.Text.TrimEnd().EndsWith("]", StringComparison.Ordinal)
            => new ChatStreamProjectionResult(
                new ChatStreamEnvelope(ChatStreamEventKind.Thinking, td.Text.Trim()),
                AccumulatedText: null),

        StreamEvent.TokenDelta td
            => new ChatStreamProjectionResult(
                new ChatStreamEnvelope(ChatStreamEventKind.Token, td.Text),
                AccumulatedText: td.Text),

        StreamEvent.ThinkingDelta tk
            => new ChatStreamProjectionResult(
                new ChatStreamEnvelope(ChatStreamEventKind.Thinking, tk.Text),
                AccumulatedText: null),

        StreamEvent.ToolUseStart ts
            => new ChatStreamProjectionResult(
                new ChatStreamEnvelope(
                    ChatStreamEventKind.ToolStart,
                    string.Empty,
                    ToolName: ts.Name,
                    ToolCallId: ts.Id),
                AccumulatedText: null),

        StreamEvent.ToolUseDelta tdj
            => new ChatStreamProjectionResult(
                new ChatStreamEnvelope(
                    ChatStreamEventKind.ToolDelta,
                    tdj.PartialJson,
                    ToolCallId: tdj.Id),
                AccumulatedText: null),

        StreamEvent.ToolUseComplete tc
            => new ChatStreamProjectionResult(
                new ChatStreamEnvelope(
                    ChatStreamEventKind.ToolComplete,
                    tc.Arguments.GetRawText(),
                    ToolName: tc.Name,
                    ToolCallId: tc.Id),
                AccumulatedText: null),

        StreamEvent.MessageComplete mc when mc.Usage is not null
            => new ChatStreamProjectionResult(
                new ChatStreamEnvelope(
                    ChatStreamEventKind.Usage,
                    // Defensive coalesce: although StreamEvent.MessageComplete.StopReason is
                    // declared non-nullable, some providers (notably custom OpenAI-compat
                    // endpoints) ship a literal null over the wire. Match the convention used
                    // by HermesChatService.StreamStructuredAsync and never let a null reach
                    // ChatStreamEnvelope.Text, which is typed non-nullable.
                    mc.StopReason ?? string.Empty,
                    Usage: mc.Usage),
                AccumulatedText: null),

        StreamEvent.StreamError err
            => new ChatStreamProjectionResult(
                new ChatStreamEnvelope(ChatStreamEventKind.Error, err.Error.Message),
                AccumulatedText: null),

        _ => new ChatStreamProjectionResult(Envelope: null, AccumulatedText: null),
    };
}
