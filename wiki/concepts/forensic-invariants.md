---
title: Forensic Invariants
type: concept
tags: [architecture, invariants]
created: 2026-04-09
updated: 2026-04-09
sources: [src/Core/agent.cs, src/compaction/CompactionSystem.cs, src/LLM/CredentialPool.cs, src/transcript/transcriptstore.cs]
---

# Forensic Invariants

Ten behavioral invariants identified from upstream analysis. Status tracked as: Implemented, Partial, or Missing.

## INV-001: Tool Loop Safety Limit

**Status: Implemented**
`Agent.MaxToolIterations = 25`. Loop in ChatAsync/StreamChatAsync counts iterations and breaks with canned fallback message. Prevents infinite tool-call loops.

## INV-002: Compression Cooldown & Iterative Summaries

**Status: Implemented**
- CompactionManager tracks `_lastCompressionFailureTime` with 600s cooldown
- Both CompactFullAsync and CompactPartialAsync check `IsInCompressionCooldown()` before attempting
- ContextManager.SummarizeEvictedAsync uses iterative template (update vs regenerate)
- Orphan sanitization via `SanitizeOrphanedToolResults` removes tool-result messages with dangling ToolCallIds
- Structured template: Goal/Progress/Decisions/Files/Next

## INV-003: Token Budget Enforcement

**Status: Implemented**
- TokenBudget with configurable maxTokens (default 8000), thresholds at 75%/94%
- SessionState.Compact() trims under Critical pressure
- CompactionConfig with ContextWindowSize=200000, thresholds at 70%/80%/90%

## INV-004: Credential Pool Rotation

**Status: Implemented**
- CredentialPool with LeastUsed/RoundRobin/Random/FillFirst strategies
- OpenAiClient retries up to 3x on 401/403/429 with key rotation
- Cooldowns: 1h for rate limits, 24h for auth errors
- Lease system for concurrent access control

## INV-005: Provider Fallback & Primary Restoration

**Status: Implemented**
- Agent maintains `_fallbackChatClient` separate from primary
- `ActivateFallback(ex)` switches on HttpRequestException
- `GetActiveChatClient()` tries primary restoration every 5 minutes
- Restoration checks `CredentialPool.HasHealthyCredentials`

## INV-006: Permission Gating

**Status: Implemented**
- PermissionManager with Allow/Deny/Ask behaviors
- Rule-based DSL: always_allow, always_deny, always_ask
- Modes: BypassPermissions, Plan (read-only), Auto, AcceptEdits, Default
- PermissionPromptCallback for interactive UI prompts

## INV-007: Secret Scanning

**Status: Implemented**
- SecretScanner with 20+ API key prefix patterns
- Checks after every tool result in both ChatAsync and StreamChatAsync
- Redacts with `[REDACTED]` or `sk-xxxx...[REDACTED]` (preserving prefix)
- Also checks: auth headers, JSON secret fields, private keys, DB connection strings, URL credentials

## INV-008: Atomic Transcript Writes

**Status: Implemented**
- TranscriptStore uses FileStream with WriteThrough + FlushAsync
- SemaphoreSlim for single-writer serialization
- Memory cache updated AFTER disk write
- Both message and activity JSONL use this pattern

## INV-009: Soul Learning from Mistakes

**Status: Implemented**
- Agent fires `RecordMistakeAsync` on tool failure (fire-and-forget Task.Run)
- MistakeEntry: Context, Mistake, Correction, Lesson
- Last 5 entries injected into soul context
- AutoDreamService consolidates learnings every 10 minutes

## INV-010: Deterministic Tool-Call IDs

**Status: Implemented**
- `NormalizeToolCallIds` generates `call_{turnNumber}_{callIndex}` for empty provider IDs
- Prevents cache invalidation when switching providers
- Applied before storing in session and transcript

## Key Files
- `src/Core/agent.cs` -- INV-001, 004, 005, 006, 009, 010
- `src/compaction/CompactionSystem.cs` -- INV-002
- `src/Context/TokenBudget.cs` -- INV-003
- `src/LLM/CredentialPool.cs` -- INV-004
- `src/security/SecretScanner.cs` -- INV-007
- `src/transcript/transcriptstore.cs` -- INV-008

## See Also
- [[version-history]]
- [[upstream-gap-analysis]]
