---
title: Agent Class
type: entity
tags: [agent, tools, streaming]
created: 2026-04-09
updated: 2026-04-09
sources: [src/Core/agent.cs]
---

# Agent Class

`src/Core/agent.cs` (~1040 lines) -- the central orchestrator implementing `IAgent`.

## Constructor

```csharp
public Agent(
    IChatClient chatClient,
    ILogger<Agent> logger,
    PermissionManager? permissions = null,
    TranscriptStore? transcripts = null,
    MemoryManager? memories = null,
    ContextManager? contextManager = null,
    SoulService? soulService = null,
    PluginManager? pluginManager = null,
    IChatClient? fallbackChatClient = null,
    CredentialPool? credentialPool = null)
```

Only `chatClient` and `logger` are required. All subsystems are optional -- Agent works without any of them, degrading gracefully.

## Key Fields

| Field | Type | Purpose |
|-------|------|---------|
| _tools | Dictionary<string, ITool> | Registered tool instances |
| _permissions | PermissionManager? | Permission gate for tool calls |
| _transcripts | TranscriptStore? | Message persistence |
| _memories | MemoryManager? | Relevant memory injection |
| _contextManager | ContextManager? | Optimized context preparation |
| _soulService | SoulService? | Identity and learning |
| _pluginManager | PluginManager? | Plugin lifecycle hooks |
| _fallbackChatClient | IChatClient? | Fallback LLM provider |
| _credentialPool | CredentialPool? | API key rotation |
| _usingFallback | bool | Currently on fallback provider |
| _fallbackActivatedAt | DateTime? | When fallback was activated |

## Key Properties

- `MaxToolIterations` -- default 25, safety limit for tool loops
- `PermissionPromptCallback` -- `Func<string, string, string?, Task<bool>>?` for interactive permission prompts. Receives `(toolName, message, toolArguments)` so the host UI can surface the literal command/args being audited.
- `ActivityLog` -- `List<ActivityEntry>` for current agent lifetime
- `ActivityEntryAdded` -- `event Action<ActivityEntry>?`
- `Tools` -- `IReadOnlyDictionary<string, ITool>`

## Constants

- `MaxParallelWorkers = 8` -- semaphore concurrency limit
- `PrimaryRestorationInterval = 5 minutes` -- how often to try restoring primary provider
- `ParallelSafeTools` -- 9 read-only tools: read_file, glob, grep, web_fetch, web_search, session_search, skill_invoke, memory, lsp
- `NeverParallelTools` -- 1 tool: ask_user

## Key Methods

| Method | Lines | Purpose |
|--------|-------|---------|
| ChatAsync | ~165-566 | Full chat loop with tool calling |
| StreamChatAsync | ~573-885 | Streaming variant yielding StreamEvent |
| GetActiveChatClient | ~102-122 | Provider fallback state machine |
| ActivateFallback | ~127-137 | Switch to fallback on error |
| ShouldParallelize | ~888-893 | Checks if batch can run parallel |
| ExecuteToolCallsParallelAsync | ~896-918 | Semaphore-gated parallel execution |
| ExecuteToolCallAsync | ~920-939 | Single tool execution with JSON deserialization |
| NormalizeToolCallIds | ~1016-1038 | Deterministic ID generation |
| BuildParameterSchema | ~941-986 | Reflection-based JSON Schema builder |
| RegisterTool | ~139-143 | Adds tool to _tools dictionary |
| GetToolDefinitions | ~148-156 | Builds ToolDefinition list for LLM |

## Key Files
- `src/Core/agent.cs` -- this class

## See Also
- [[../systems/agent-loop]]
- [[chat-client-interface]]
