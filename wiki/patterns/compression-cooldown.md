---
title: Compression Cooldown Pattern
type: pattern
tags: [compaction, context, resilience]
created: 2026-04-09
updated: 2026-04-09
sources: [src/compaction/CompactionSystem.cs, src/Context/ContextManager.cs]
---

# Compression Cooldown Pattern

INV-002: After a compression failure, wait 600 seconds before retrying to prevent death spirals.

## Problem

When the LLM fails to summarize evicted messages (network error, rate limit, malformed response), immediately retrying on the next turn creates a feedback loop:
1. Compression fails
2. Next turn: context is even larger because more messages accumulated
3. Compression fails again (higher token count, same underlying issue)
4. Repeat until context window is blown

## Solution

Track `_lastCompressionFailureTime`. Before any compression attempt, check:

```csharp
public bool IsInCompressionCooldown()
{
    if (_lastCompressionFailureTime is null) return false;
    var elapsed = DateTime.UtcNow - _lastCompressionFailureTime.Value;
    return elapsed < CompressionCooldown; // 600 seconds
}
```

On success: reset `_lastCompressionFailureTime = null`
On failure: set `_lastCompressionFailureTime = DateTime.UtcNow`

## Implementations

### CompactionManager (src/compaction/CompactionSystem.cs)

Both `CompactFullAsync` and `CompactPartialAsync` check `IsInCompressionCooldown()` at entry. If in cooldown, they return the original messages unchanged (no-op). On success, cooldown resets. On exception, cooldown activates.

### ContextManager (src/Context/ContextManager.cs)

`SummarizeEvictedAsync` catches exceptions and logs error but does NOT enter a formal cooldown -- it keeps the stale summary. The CompactionManager provides the formal cooldown pattern.

## Related Invariants

- INV-002: Iterative summaries -- update existing summary instead of regenerating
- Orphan sanitization: after compaction, `SanitizeOrphanedToolResults` removes tool-result messages whose ToolCallId references a summarized-away tool_call

## When to Use

Apply this pattern whenever:
- An LLM call is used for compressing/summarizing context
- Failure could lead to repeated retries with growing input
- The operation is not user-visible (background maintenance)

## Test Criteria

1. After failure, `IsInCompressionCooldown()` returns true for 600s
2. After success, cooldown resets immediately
3. During cooldown, compaction returns original messages unmodified
4. After cooldown expires, compaction resumes normally

## Key Files
- `src/compaction/CompactionSystem.cs` -- CompactionManager with cooldown
- `src/Context/ContextManager.cs` -- SummarizeEvictedAsync

## See Also
- [[../systems/context-management]]
