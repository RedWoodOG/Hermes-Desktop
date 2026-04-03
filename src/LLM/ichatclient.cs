namespace Hermes.Agent.LLM;

using Hermes.Agent.Core;

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
}

public sealed class LlmConfig
{
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
}
