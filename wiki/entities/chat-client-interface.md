---
title: IChatClient Interface
type: entity
tags: [llm, streaming, tools]
created: 2026-04-09
updated: 2026-04-09
sources: [src/LLM/ichatclient.cs, src/LLM/StreamTypes.cs, src/Core/models.cs]
---

# IChatClient Interface

`src/LLM/ichatclient.cs` -- the abstraction over LLM providers.

## Interface Definition

```csharp
public interface IChatClient
{
    Task<string> CompleteAsync(IEnumerable<Message> messages, CancellationToken ct);

    Task<ChatResponse> CompleteWithToolsAsync(
        IEnumerable<Message> messages,
        IEnumerable<ToolDefinition> tools,
        CancellationToken ct);

    IAsyncEnumerable<string> StreamAsync(
        IEnumerable<Message> messages, CancellationToken ct);

    IAsyncEnumerable<StreamEvent> StreamAsync(
        string? systemPrompt,
        IEnumerable<Message> messages,
        IEnumerable<ToolDefinition>? tools = null,
        CancellationToken ct = default);
}
```

## ChatResponse

```csharp
public sealed class ChatResponse {
    public string? Content { get; init; }        // null when only tool calls
    public List<ToolCall>? ToolCalls { get; init; }  // null when finish_reason is "stop"
    public string? FinishReason { get; init; }   // "stop", "tool_calls", "length"
    public bool HasToolCalls => ToolCalls is { Count: > 0 };
}
```

## ToolDefinition

```csharp
public sealed class ToolDefinition {
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonElement Parameters { get; init; }  // JSON Schema
}
```

## ToolCall

```csharp
public sealed class ToolCall {
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Arguments { get; init; }  // JSON-encoded
}
```

## StreamEvent Hierarchy

All are sealed records inheriting from abstract record StreamEvent:

| Event | Fields | When |
|-------|--------|------|
| TokenDelta | Text | Regular text token received |
| ThinkingDelta | Text | Reasoning content (extended thinking models) |
| ToolUseStart | Id, Name | Tool call begins streaming |
| ToolUseDelta | Id, PartialJson | Partial tool argument JSON |
| ToolUseComplete | Id, Name, Arguments(JsonElement) | Full tool call ready |
| MessageComplete | StopReason, Usage? | Stream finished |
| StreamError | Exception | Error during stream |

## UsageStats

```csharp
public sealed record UsageStats(
    int InputTokens, int OutputTokens,
    int? CacheCreationTokens = null,
    int? CacheReadTokens = null);
```

## LlmConfig

```csharp
public sealed class LlmConfig {
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public double Temperature { get; init; } = 0.7;
    public int MaxTokens { get; init; } = 4096;
}
```

## Implementations

- **OpenAiClient** -- OpenAI-compatible (also Ollama, MiniMax, DeepSeek)
- **AnthropicClient** -- Anthropic Claude API

## Key Files
- `src/LLM/ichatclient.cs` -- interface + LlmConfig
- `src/LLM/StreamTypes.cs` -- StreamEvent, UsageStats, StreamedMessage
- `src/Core/models.cs` -- Message, ToolCall, ToolDefinition, ChatResponse

## See Also
- [[../systems/llm-providers]]
- [[agent-class]]
