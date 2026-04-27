namespace Hermes.Agent.Core;

using System.Text.Json;

public sealed class Message
{
    public required string Role { get; init; }
    public required string Content { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    /// <summary>Tool calls requested by the assistant in this message.</summary>
    public List<ToolCall>? ToolCalls { get; init; }
}

public sealed class Session
{
    public required string Id { get; init; }
    public string? UserId { get; init; }
    public string? Platform { get; init; }
    public List<Message> Messages { get; init; } = new();
    public Dictionary<string, object> State { get; init; } = new();
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public void AddMessage(Message message)
    {
        Messages.Add(message);
        LastActivityAt = DateTime.UtcNow;
    }
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
}

public sealed class ToolResult
{
    public bool Success { get; init; }
    public required string Content { get; init; }
    public Exception? Error { get; init; }

    public static ToolResult Ok(string content) => new() { Success = true, Content = content };
    public static ToolResult Fail(string error, Exception? ex = null) => new() { Success = false, Content = error, Error = ex };
}

public interface ITool
{
    string Name { get; }
    string Description { get; }
    Type ParametersType { get; }
    Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct);
}

/// <summary>
/// Optional extension for tools that already have a provider-native JSON schema.
/// </summary>
public interface IToolSchemaProvider
{
    JsonElement? GetParameterSchema();
}

public interface IAgent
{
    Task<string> ChatAsync(string message, Session session, CancellationToken ct);
    IAsyncEnumerable<Hermes.Agent.LLM.StreamEvent> StreamChatAsync(string message, Session session, CancellationToken ct);
    void RegisterTool(ITool tool);
}

// ── Tool Calling Types ──

/// <summary>A tool call requested by the LLM.</summary>
public sealed class ToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>JSON-encoded arguments string.</summary>
    public required string Arguments { get; init; }
}

/// <summary>Definition sent to the LLM so it knows what tools are available.</summary>
public sealed class ToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    /// <summary>JSON Schema for the tool's parameters.</summary>
    public required JsonElement Parameters { get; init; }
}

/// <summary>Structured response from the LLM that may contain tool calls.</summary>
public sealed class ChatResponse
{
    /// <summary>Text content (null when the LLM only emits tool calls).</summary>
    public string? Content { get; init; }
    /// <summary>Tool calls the LLM wants to invoke (null when finish_reason is "stop").</summary>
    public List<ToolCall>? ToolCalls { get; init; }
    /// <summary>Why the LLM stopped: "stop", "tool_calls", "length", etc.</summary>
    public string? FinishReason { get; init; }

    public bool HasToolCalls => ToolCalls is { Count: > 0 };
}
