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
    /// Completion with system context passed structurally. The conversation
    /// list must not contain <c>role: "system"</c> entries; layered system
    /// content goes in <paramref name="system"/>.
    /// </summary>
    Task<ChatResponse> CompleteWithToolsAsync(
        SystemContext system,
        IEnumerable<Message> conversation,
        IEnumerable<ToolDefinition> tools,
        CancellationToken ct)
        => CompleteWithToolsAsync(BridgeToLegacy(system, conversation), tools, ct);

    /// <summary>
    /// Streaming with system context passed structurally. The conversation
    /// list must not contain <c>role: "system"</c> entries; layered system
    /// content goes in <paramref name="system"/>.
    /// </summary>
    IAsyncEnumerable<StreamEvent> StreamAsync(
        SystemContext system,
        IEnumerable<Message> conversation,
        IEnumerable<ToolDefinition>? tools = null,
        CancellationToken ct = default)
        => StreamAsync(systemPrompt: null, BridgeToLegacy(system, conversation), tools, ct);

    /// <summary>
    /// Coalesces a SystemContext into a single leading system message and
    /// prepends it to the conversation. Guarantees the resulting sequence
    /// contains zero mid-list system messages — the load-bearing invariant
    /// strict OpenAI-compatible servers depend on.
    /// </summary>
    private static IEnumerable<Message> BridgeToLegacy(
        SystemContext system,
        IEnumerable<Message> conversation)
    {
        if (!system.IsEmpty)
            yield return new Message { Role = "system", Content = system.Render("\n\n") };
        foreach (var m in conversation)
            yield return m;
    }
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
