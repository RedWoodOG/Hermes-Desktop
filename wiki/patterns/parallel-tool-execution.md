---
title: Parallel Tool Execution Pattern
type: pattern
tags: [tools, concurrency, agent]
created: 2026-04-09
updated: 2026-04-09
sources: [src/Core/agent.cs]
---

# Parallel Tool Execution Pattern

When multiple tool calls arrive in one LLM response, read-only tools can run concurrently to reduce latency.

## Decision Logic

```csharp
private static bool ShouldParallelize(IReadOnlyList<ToolCall> toolCalls)
{
    if (toolCalls.Count <= 1) return false;
    if (toolCalls.Any(tc => NeverParallelTools.Contains(tc.Name))) return false;
    return toolCalls.All(tc => ParallelSafeTools.Contains(tc.Name));
}
```

Three conditions must ALL be true:
1. More than one tool call in the batch
2. No tool in the NeverParallel set is present
3. ALL tools are in the ParallelSafe set

If any tool is outside both sets (e.g., write_file, bash), the entire batch runs sequentially.

## Tool Sets

**ParallelSafeTools** (9 read-only tools):
`read_file, glob, grep, web_fetch, web_search, session_search, skill_invoke, memory, lsp`

**NeverParallelTools** (1 tool):
`ask_user`

## Execution

```csharp
private async Task<List<(ToolCall, ToolResult, long)>> ExecuteToolCallsParallelAsync(
    IReadOnlyList<ToolCall> toolCalls, CancellationToken ct)
{
    using var semaphore = new SemaphoreSlim(MaxParallelWorkers); // 8
    var tasks = toolCalls.Select(async toolCall =>
    {
        await semaphore.WaitAsync(ct);
        try
        {
            var sw = Stopwatch.StartNew();
            var result = await ExecuteToolCallAsync(toolCall, ct);
            sw.Stop();
            return (toolCall, result, sw.ElapsedMilliseconds);
        }
        finally { semaphore.Release(); }
    }).ToList();
    return (await Task.WhenAll(tasks)).ToList();
}
```

Semaphore limits concurrency to 8 workers. Results are collected in order and processed sequentially for activity logging and secret scanning.

## Sequential Path Differences

When NOT parallelized:
- Each tool goes through permission gate (Allow/Deny/Ask)
- Activity entry updated in real-time (Running -> Success/Failed)
- Mistakes recorded to SoulService on failure
- Secret scanning on each result individually

When parallelized:
- NO permission checking (parallel path skips it)
- Activity entries created after all complete
- No mistake recording in parallel path

## Streaming Limitation

`StreamChatAsync` does NOT use parallel execution -- all tools run sequentially with full permission gates and UI status feedback.

## When to Extend

To add a tool to ParallelSafeTools:
1. Verify it is truly read-only (no side effects)
2. Verify it is thread-safe (no shared mutable state)
3. Add to the HashSet in Agent class
4. Consider whether parallel execution changes the tool's semantics

## Key Files
- `src/Core/agent.cs` -- ShouldParallelize (~line 888), ExecuteToolCallsParallelAsync (~line 896)

## See Also
- [[../systems/tool-system]]
- [[../systems/agent-loop]]
