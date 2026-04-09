---
title: Context Management
type: system
tags: [context, compaction, prompt]
created: 2026-04-09
updated: 2026-04-09
sources: [src/Context/ContextManager.cs, src/Context/PromptBuilder.cs, src/Context/TokenBudget.cs, src/Context/SessionState.cs, src/compaction/CompactionSystem.cs]
---

# Context Management

Replaces the naive "send all messages" pattern with structured context: archive (TranscriptStore) + active memory (SessionState) + selective recall.

## 6-Layer Prompt Architecture

PromptBuilder.ToOpenAiMessages assembles layers in this order:

| Layer | Role | Content | Cache behavior |
|-------|------|---------|---------------|
| 0 | system | Soul context (identity, user profile, project rules, habits) | Rarely changes -- excellent anchor |
| 1 | system | Stable system prompt (instructions) | Never changes |
| 2 | system | `[Session State]` JSON (goal, decisions, entities, summary) | Changes slowly |
| 3 | system | `[Retrieved Context]` chunks joined by `---` | Per-query |
| 4 | user/assistant | Recent conversation turns (sliding window) | Changes every turn |
| 5 | user | Current user message | Changes every turn |

## TokenBudget

- Default: `maxTokens=8000`, `recentTurnWindow=6`
- Thresholds: Normal (<75%), High (75-94%), Critical (>94%)
- Estimation: ~4 chars/token + 4 tokens/message for role framing
- `TrimToRecentWindow` scans backwards counting user messages as turn boundaries
- `GetEvictedMessages` returns messages before the window start

## SessionState

Structured rolling memory replacing raw transcript:
- `ActiveGoal` -- current task objective
- `Constraints` -- rules/boundaries
- `Decisions` -- list of Decision{What, Why, TurnNumber}
- `OpenQuestions` -- unresolved items
- `ImportantEntities` -- key names, paths, terms
- `Summary` -- ContextSummary{Content, CoveredThroughTurn}
- `PreviousResponseId` -- for OpenAI cache chaining

`Compact()` trims to maxDecisions=5, maxQuestions=3, maxEntities=10 under Critical pressure.

## ContextManager.PrepareContextAsync

1. Load or create SessionState (ConcurrentDictionary with per-session SemaphoreSlim)
2. Load transcript, split into recent + evicted via TokenBudget
3. Estimate total tokens across all layers
4. Summarize evicted if: High pressure, first eviction, or summary stale >10 turns
5. Under Critical pressure: compact SessionState itself
6. Load soul context from SoulService
7. Build PromptPacket and convert to OpenAI messages

## Iterative Summarization (INV-002)

When a previous summary exists, the LLM is told to UPDATE it, not regenerate. Template:
`- Goal / Progress / Decisions / Files / Next`

## CompactionManager (src/compaction/)

Separate system with 600s cooldown pattern (INV-002):
- `CompactFullAsync` -- summarizes entire conversation
- `CompactPartialAsync` -- summarizes older messages, keeps recent N
- `CompactMicro` -- removes old tool results by age
- `SanitizeOrphanedToolResults` -- removes tool-result messages whose ToolCallId was summarized away

Config defaults: ContextWindowSize=200000, CompactionThreshold=0.80, CriticalThreshold=0.90.

## Key Files
- `src/Context/ContextManager.cs` -- orchestrator, PrepareContextAsync
- `src/Context/PromptBuilder.cs` -- 6-layer assembly, cache anchoring
- `src/Context/TokenBudget.cs` -- budget enforcement, pressure levels
- `src/Context/SessionState.cs` -- structured state, Decision, ContextSummary
- `src/compaction/CompactionSystem.cs` -- CompactionManager, cooldown, orphan sanitization

## See Also
- [[../patterns/compression-cooldown]]
- [[agent-loop]]
