---
title: Agent Loop
type: system
tags: [agent, tools, streaming, permissions]
created: 2026-04-09
updated: 2026-04-09
sources: [src/Core/agent.cs, src/Core/models.cs, src/Core/ActivityEntry.cs]
---

# Agent Loop

The Agent class (`src/Core/agent.cs`, ~1040 lines) is the central orchestrator. It implements `IAgent` with two entry points: `ChatAsync` (blocking) and `StreamChatAsync` (IAsyncEnumerable<StreamEvent>).

## ChatAsync Flow

1. **Plugin turn start** -- calls `_pluginManager.OnTurnStartAsync()` if available
2. **Memory injection** -- loads relevant memories via PluginManager (preferred) or MemoryManager (fallback). Injected as first system message.
3. **User message** -- added to session, persisted to TranscriptStore
4. **Soul injection** -- if no ContextManager, injects soul context directly as first system message
5. **Context preparation** -- if ContextManager exists, calls `PrepareContextAsync()` for optimized context; falls back to raw `session.Messages`
6. **Tool loop** -- iterates up to `MaxToolIterations` (default 25):
   - Calls `CompleteWithToolsAsync` on active client (with fallback on HttpRequestException)
   - If no tool calls: saves final message, returns text
   - Normalizes tool-call IDs via `NormalizeToolCallIds` (deterministic `call_{turn}_{index}` fallback)
   - Decides parallel vs sequential via `ShouldParallelize`
   - For each tool: permission gate -> activity tracking -> execute -> secret scan -> save result
7. **Fallback** -- if MaxToolIterations hit, returns canned message

## StreamChatAsync Differences

- Tool-calling turns use non-streaming `CompleteWithToolsAsync` (same as Python upstream)
- Final text response is emitted as a single `StreamEvent.TokenDelta`
- Tool status emitted as `[Calling tool: {name}]` delta for UI feedback
- No parallel execution path in streaming (tools run sequentially with permission gates)

## Permission Gating

Three outcomes from `PermissionManager.CheckPermissionsAsync`:
- **Allow** -- execute immediately
- **Deny** -- log denial, inject denial message as tool result, skip
- **Ask** -- invoke `PermissionPromptCallback(toolName, message)`. Null callback = deny.

## Activity Logging

Every tool call produces an `ActivityEntry` with: ToolName, ToolCallId, InputSummary (200 chars), OutputSummary (200 chars), Status (Running/Success/Failed/Denied), DurationMs. Entries are added to `ActivityLog` list and emitted via `ActivityEntryAdded` event. Also persisted to session activity JSONL.

## Parallel Execution

`ShouldParallelize` returns true when: count > 1, no NeverParallelTools present, ALL tools are in ParallelSafeTools set. Execution uses `SemaphoreSlim(MaxParallelWorkers=8)` with `Task.WhenAll`.

## Provider Fallback (INV-004/005)

`GetActiveChatClient()` checks if on fallback and whether `PrimaryRestorationInterval` (5 min) has elapsed. On HttpRequestException, `ActivateFallback()` switches to `_fallbackChatClient`.

## Key Files
- `src/Core/agent.cs` -- Agent class, ChatAsync, StreamChatAsync, tool execution
- `src/Core/models.cs` -- Message, Session, ITool, ToolCall, ToolDefinition, ChatResponse
- `src/Core/ActivityEntry.cs` -- ActivityEntry, ActivityStatus enum

## See Also
- [[../entities/agent-class]]
- [[../patterns/parallel-tool-execution]]
- [[../patterns/provider-fallback]]
