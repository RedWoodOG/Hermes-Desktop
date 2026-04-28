namespace Hermes.Agent.LLM;

using Hermes.Agent.Core;
using System.Runtime.CompilerServices;
using System.Text.Json;

public interface IChatClient
{
    /// <summary>Simple text completion (no tool calling).</summary>
    Task<string> CompleteAsync(IEnumerable<Message> messages, CancellationToken ct);

    /// <summary>Completion with tool definitions — returns structured response that may contain tool calls.</summary>
    Task<ChatResponse> CompleteWithToolsAsync(
        IEnumerable<Message> messages,
        IEnumerable<ToolDefinition> tools,
        CancellationToken ct);

    /// <summary>Streaming completion — yields tokens as they arrive.</summary>
    IAsyncEnumerable<string> StreamAsync(IEnumerable<Message> messages, CancellationToken ct);

    /// <summary>Streaming with system prompt, tools, and structured events.</summary>
    IAsyncEnumerable<StreamEvent> StreamAsync(
        string? systemPrompt,
        IEnumerable<Message> messages,
        IEnumerable<ToolDefinition>? tools = null,
        CancellationToken ct = default);

    // ── SystemContext-aware overloads ──
    //
    // Default implementations bridge to the legacy methods by emitting a
    // single coalesced leading system message. This is byte-equivalent to
    // current behavior for AnthropicClient (its extractor finds the one
    // coalesced system and routes it to Anthropic's `system` param) and a
    // strict-spec fix for OpenAiClient (one leading system, none mid-list)
    // which unblocks vLLM/Qwen, llama.cpp strict templates, TGI, and
    // LMStudio strict-template models.
    //
    // Providers may override these for native rendering (e.g. Anthropic
    // can later send each layer as a separate cache_control block to
    // unlock prompt caching on stable layers).

    /// <summary>
    /// Simple completion with system context passed structurally. Stray
    /// <c>role: "system"</c> entries inside <paramref name="conversation"/>
    /// are hoisted into the coalesced system block.
    /// </summary>
    Task<string> CompleteAsync(
        SystemContext system,
        IEnumerable<Message> conversation,
        CancellationToken ct)
    {
        var prepared = PrepareLegacyCall(system, conversation);
        return CompleteAsync(prepared.Messages, ct);
    }

    /// <summary>
    /// Completion with system context passed structurally. Stray
    /// <c>role: "system"</c> entries inside <paramref name="conversation"/>
    /// are hoisted into the coalesced system block before the call reaches
    /// the legacy method, so the bridge truly guarantees zero mid-list
    /// system messages on the wire.
    /// </summary>
    Task<ChatResponse> CompleteWithToolsAsync(
        SystemContext system,
        IEnumerable<Message> conversation,
        IEnumerable<ToolDefinition> tools,
        CancellationToken ct)
    {
        var prepared = PrepareLegacyCall(system, conversation);
        return CompleteWithToolsAsync(prepared.Messages, tools, ct);
    }

    /// <summary>
    /// Streaming with system context passed structurally. Threads the
    /// rendered system content into both the legacy <c>systemPrompt</c>
    /// parameter (required by AnthropicClient — its streaming
    /// <c>BuildPayload</c> drops <c>role:"system"</c> entries from the
    /// messages list and only emits top-level <c>system</c> when
    /// <c>systemPrompt</c> is non-empty) and as a leading system message
    /// in the conversation (required by OpenAiClient — its streaming
    /// path ignores <c>systemPrompt</c> and reads the messages list
    /// verbatim). Stray <c>role:"system"</c> entries inside
    /// <paramref name="conversation"/> are hoisted into the system block.
    /// </summary>
    IAsyncEnumerable<StreamEvent> StreamAsync(
        SystemContext system,
        IEnumerable<Message> conversation,
        IEnumerable<ToolDefinition>? tools = null,
        CancellationToken ct = default)
    {
        var prepared = PrepareLegacyCall(system, conversation);
        return StreamAsync(prepared.SystemPrompt, prepared.Messages, tools, ct);
    }

    /// <summary>
    /// Delegates to <see cref="SystemContext.CoalesceWith"/> — kept as a
    /// thin alias so existing tests that reference PrepareLegacyCall via
    /// reflection still work, and so the bridge contract reads naturally
    /// inside the interface body.
    /// </summary>
    private static (string? SystemPrompt, IEnumerable<Message> Messages) PrepareLegacyCall(
        SystemContext system,
        IEnumerable<Message> conversation)
        => system.CoalesceWith(conversation);
}

public sealed class LlmConfig
{
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public string? AuthMode { get; init; }
    public string? AuthHeader { get; init; }
    public string? AuthScheme { get; init; }
    public string? ApiKeyEnv { get; init; }
    public string? AuthTokenEnv { get; init; }
    public string? AuthTokenCommand { get; init; }
    public double Temperature { get; init; } = 0.7;
    public int MaxTokens { get; init; } = 4096;
}

// ToolDefinition is defined in Hermes.Agent.Core.ToolDefinition
