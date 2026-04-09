---
title: Transcript System
type: system
tags: [transcript, persistence]
created: 2026-04-09
updated: 2026-04-09
sources: [src/transcript/transcriptstore.cs]
---

# Transcript System

Transcript-first persistence: every message is written to disk BEFORE updating in-memory state.

## TranscriptStore

Constructor: `TranscriptStore(string transcriptsDir, bool eagerFlush = false)`

### Write Path (INV-008)

`SaveMessageAsync` uses atomic writes:
1. Serialize message to JSON
2. Acquire `_writeLock` (SemaphoreSlim)
3. Open FileStream with `FileOptions.WriteThrough | FileOptions.SequentialScan`
4. Write UTF-8 bytes + newline
5. `FlushAsync` to ensure disk persistence
6. Release lock
7. THEN update in-memory `ConcurrentDictionary<string, List<Message>>` cache

WriteThrough ensures the OS bypasses the write cache -- data hits disk immediately.

### Read Path

`LoadSessionAsync`:
1. Check in-memory cache first
2. If not cached, read JSONL file line-by-line
3. Deserialize each non-empty line as Message
4. Cache result for subsequent reads

### Activity Logging

Separate JSONL file per session: `{sessionId}.activity.jsonl`
- `SaveActivityAsync(sessionId, ActivityEntry, ct)` -- same WriteThrough pattern
- `LoadActivityAsync(sessionId, ct)` -- reads all activity entries

### Session Management

- `SessionExists(id)` -- checks cache then disk
- `GetAllSessionIds()` -- union of cache keys and `.jsonl` filenames
- `DeleteSessionAsync(id, ct)` -- removes both transcript and activity JSONL, clears cache
- `ClearCache()` -- frees memory, keeps disk

### File Layout

```
transcripts/
  {sessionId}.jsonl           -- message transcript
  {sessionId}.activity.jsonl  -- tool activity log
```

## ResumeManager

Restores sessions from transcript:
- `ResumeSessionAsync(sessionId, ct)` -- loads messages, creates Session object with restored timestamps
- `ListSessionsAsync(ct)` -- returns SessionSummary (id, count, dates, first message preview)

## SessionHistory

Separate from transcripts -- tracks commands/prompts for up-arrow navigation:
- `AddToHistory(command, project, sessionId?)` -- pending entries + auto-flush timer (100ms)
- `GetHistoryAsync()` -- pending entries first, then from file
- `RemoveLastAsync(ct)` -- undo last entry
- MAX_HISTORY_ITEMS = 100, MAX_PASTED_CONTENT_LENGTH = 1024

## Key Files
- `src/transcript/transcriptstore.cs` -- TranscriptStore, ResumeManager, SessionHistory (~493 lines)

## See Also
- [[../patterns/atomic-persistence]]
- [[agent-loop]]
