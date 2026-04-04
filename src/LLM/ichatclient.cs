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

    /// <summary>Streaming completion with structured events.</summary>
    IAsyncEnumerable<StreamEvent> StreamAsync(IEnumerable<Message> messages, CancellationToken ct = default);

    /// <summary>Streaming with system prompt and tools.</summary>
    IAsyncEnumerable<StreamEvent> StreamAsync(
        string? systemPrompt,
        IEnumerable<Message> messages,
        IEnumerable<ToolDefinition>? tools = null,
        CancellationToken ct = default);
}

public sealed class LlmConfig
{
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public double Temperature { get; init; } = 0.7;
    public int MaxTokens { get; init; } = 4096;
}

/// <summary>
/// Tool definition for LLM function calling.
/// </summary>
public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonElement InputSchema);
