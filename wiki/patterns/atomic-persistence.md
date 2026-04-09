---
title: Atomic Persistence Pattern
type: pattern
tags: [persistence, transcript, skills]
created: 2026-04-09
updated: 2026-04-09
sources: [src/transcript/transcriptstore.cs, src/skills/skillmanager.cs]
---

# Atomic Persistence Pattern

Two variants used across the codebase: WriteThrough for append-only JSONL and temp-file-rename for full-file writes.

## Pattern 1: WriteThrough JSONL (TranscriptStore)

Used for transcript messages and activity entries where data must survive crashes.

```csharp
await _writeLock.WaitAsync(ct);
try
{
    using var fs = new FileStream(
        path,
        FileMode.Append,
        FileAccess.Write,
        FileShare.Read,
        bufferSize: 4096,
        FileOptions.WriteThrough | FileOptions.SequentialScan);
    await fs.WriteAsync(bytes, ct);
    await fs.FlushAsync(ct);
}
finally { _writeLock.Release(); }
```

Key properties:
- `FileOptions.WriteThrough` -- OS bypasses write cache, data hits physical disk
- `FileShare.Read` -- concurrent readers allowed while writing
- `SemaphoreSlim(1,1)` -- single writer at a time
- Memory cache updated AFTER disk write succeeds

This ensures the on-disk transcript is always the source of truth. If the process crashes mid-write, the JSONL file may have a truncated last line, but all previous lines are intact.

## Pattern 2: Temp-File-Rename (SkillManager)

Used for full-file writes where partial content would be corrupted.

```csharp
var tempPath = path + ".tmp";
try
{
    await File.WriteAllTextAsync(tempPath, content, ct);
    File.Move(tempPath, path, overwrite: true);
}
catch
{
    if (File.Exists(tempPath)) File.Delete(tempPath);
    throw;
}
```

Key properties:
- Write to `.tmp` file first
- Rename is atomic on NTFS/ext4 (single directory entry update)
- On failure: clean up temp file, original untouched
- Post-write: SecretScanner validates, rolls back if secrets found

## Pattern 3: Backup-and-Rollback (Skill Edit/Patch)

For mutations on existing files:
1. Read original content as backup
2. Write new content via temp-file-rename
3. SecretScanner validates
4. On secret detection: restore backup

## Why Not Direct Append?

Direct `File.AppendAllTextAsync` is NOT crash-safe because:
- OS may buffer writes in memory
- Power loss loses buffered data
- No guarantee of write ordering
- WriteThrough forces synchronous disk writes

## JSONL Format Benefits

- Each line is a complete, self-contained JSON object
- Truncated last line is detectable and skippable
- No need to parse entire file to append
- Easy to stream-process for large files

## Where Applied

| Location | Pattern | Purpose |
|----------|---------|---------|
| TranscriptStore.SaveMessageAsync | WriteThrough | Message persistence |
| TranscriptStore.SaveActivityAsync | WriteThrough | Activity log persistence |
| SoulService.RecordMistakeAsync | Direct append | Mistake journal (no WriteThrough) |
| SoulService.RecordHabitAsync | Direct append | Habit journal (no WriteThrough) |
| SkillManager.CreateSkillAsync | Temp-file-rename | New skill creation |
| SkillManager.EditSkillAsync | Backup + temp-file-rename | Skill modification |
| SkillManager.PatchSkillAsync | Backup + temp-file-rename | Skill patching |

Note: SoulService uses plain `File.AppendAllTextAsync` for journals -- a potential gap where WriteThrough would be more crash-safe.

## Key Files
- `src/transcript/transcriptstore.cs` -- WriteThrough pattern
- `src/skills/skillmanager.cs` -- temp-file-rename pattern

## See Also
- [[../systems/transcript-system]]
- [[../systems/skill-system]]
