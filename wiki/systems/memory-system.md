---
title: Memory System
type: system
tags: [memory, context]
created: 2026-04-09
updated: 2026-04-09
sources: [src/memory/memorymanager.cs, src/plugins/BuiltinMemoryPlugin.cs]
---

# Memory System

File-based persistent memory with LLM-powered relevance filtering and freshness warnings.

## Storage Location

`~/.hermes-cs/projects/<git-root>/memory/` -- each memory is a `.md` file with YAML frontmatter.

## MemoryManager

Constructor: `MemoryManager(string memoryDir, IChatClient chatClient, ILogger logger)`

### LoadRelevantMemoriesAsync Flow

1. **Scan** -- `ScanMemoryFilesAsync`: finds all `*.md` files (excluding MEMORY.md), parses YAML frontmatter from first 30 lines, caps at 200 files, sorts by mtime descending
2. **Filter** -- removes already-surfaced memories (tracked by `AlreadySurfaced` flag)
3. **Select** -- `SelectRelevantMemoriesAsync`: if <=5 candidates, returns all. Otherwise, builds manifest of `[type] filename: description` and asks LLM to pick up to 5 most relevant
4. **Load** -- reads full content of selected files, adds freshness warnings

### Freshness Warnings

Based on file modification time:
- <1 day: no warning
- 1-6 days: "{N} days old"
- 7-29 days: "{N} weeks old"
- 30+ days: "{N} months old"

Warning text: `<system-reminder>This memory is {age} old. Memories are point-in-time observations, not live state. Verify against current code before asserting as fact.</system-reminder>`

### Memory Format

```yaml
---
name: My Memory
description: One-line description
type: user  # user, feedback, project, reference
---
```

Types: `user` (default), `feedback`, `project`, `reference`

### CRUD Operations

- `SaveMemoryAsync(filename, content, type, ct)` -- auto-adds frontmatter if missing
- `DeleteMemoryAsync(filename, ct)` -- removes file
- `LoadAllMemoriesAsync(ct)` -- returns all for management UI

## Injection into Agent

Two paths:
1. **Via PluginManager** -- `BuiltinMemoryPlugin.GetSystemPromptBlocksAsync` calls MemoryManager
2. **Direct fallback** -- Agent.ChatAsync injects as first system message: `[Relevant Memories]\n{memoryBlock}`

Memory block format: `[{type}] {filename}:\n{content}` separated by `---`

## YAML Helper

Simple property-based YAML parser in `Yaml.Deserialize<T>()` -- splits lines, finds `key: value` pairs, matches to properties by name (case-insensitive).

## Key Files
- `src/memory/memorymanager.cs` -- MemoryManager, MemoryContext, MemoryHeader, Yaml helper (~363 lines)
- `src/plugins/BuiltinMemoryPlugin.cs` -- plugin bridge for memory injection

## See Also
- [[soul-system]]
- [[context-management]]
