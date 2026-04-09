---
title: Soul System
type: system
tags: [soul, identity, learning]
created: 2026-04-09
updated: 2026-04-09
sources: [src/soul/SoulService.cs, src/soul/SoulModels.cs, src/soul/SoulExtractor.cs, src/soul/AgentProfile.cs, src/dream/autodreamservice.cs]
---

# Soul System

Persistent identity, user understanding, project rules, and learning from mistakes/habits.

## File Layout (under hermesHome)

```
SOUL.md              -- Agent identity (global, ~80 lines default template)
USER.md              -- User profile (global, template with sections)
soul/
  mistakes.jsonl     -- Append-only mistake journal
  habits.jsonl       -- Append-only habit journal
projects/{dir}/
  AGENTS.md          -- Per-project rules
```

## SoulService

Constructor: `SoulService(string hermesHome, ILogger<SoulService> logger)`

Key methods:
- `LoadFileAsync(SoulFileType, projectDir?)` -- loads SOUL.md, USER.md, or AGENTS.md
- `SaveFileAsync(SoulFileType, content, projectDir?)` -- saves soul files
- `RecordMistakeAsync(MistakeEntry)` -- appends to mistakes.jsonl
- `RecordHabitAsync(HabitEntry)` -- appends to habits.jsonl
- `AssembleSoulContextAsync(projectDir?)` -- builds full context string
- `IsFirstRun()` -- checks for `<!-- UNCONFIGURED -->` marker

## AssembleSoulContextAsync

Builds 5 sections with size caps:
1. `[Agent Identity]` -- full SOUL.md content
2. `[User Profile]` -- USER.md truncated to 1500 chars (skipped if <50 chars)
3. `[Project Rules]` -- AGENTS.md truncated to 1500 chars (skipped if <50 chars)
4. `[Learned from Mistakes]` -- last 5 entries, lesson field only
5. `[Good Habits]` -- last 5 entries, habit field only

Hard cap: 6000 chars total (~1500 tokens). Truncated with `[...soul context truncated]`.

## SoulModels

MistakeEntry: `{ Timestamp, Context, Mistake, Correction, Lesson }` -- JSONL serialized
HabitEntry: `{ Timestamp, Context, Habit, PositiveFeedback }` -- JSONL serialized
SoulExtractionResult: `{ Mistakes[], Habits[], UserProfileUpdate? }`
SoulFileType enum: Soul, User, ProjectRules

## Default SOUL.md Template

The template defines Hermes as a native Windows AI agent with:
- Core values: genuinely helpful, honest, transparent, learning, safe
- Working style: read before edit, search first, test changes, minimal changes
- Communication: lead with answer, treat users as intelligent adults

## AutoDreamService

Background service (`BackgroundService`) running every 10 minutes:
- Scans session transcripts for learnings
- Uses SoulExtractor to find mistakes, habits, and user profile signals
- Consolidates into persistent soul files
- Enabled via config flag

## Integration Points

- **Agent.ChatAsync** -- on tool failure, fires `RecordMistakeAsync` as fire-and-forget
- **ContextManager** -- calls `AssembleSoulContextAsync` during PrepareContextAsync
- **Agent fallback path** -- injects soul context directly as first system message when no ContextManager

## Key Files
- `src/soul/SoulService.cs` -- main service (~310 lines)
- `src/soul/SoulModels.cs` -- MistakeEntry, HabitEntry, SoulFileType
- `src/soul/SoulExtractor.cs` -- extracts learnings from transcripts
- `src/dream/autodreamservice.cs` -- background consolidation

## See Also
- [[context-management]]
- [[memory-system]]
