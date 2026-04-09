---
title: Wiki Schema
type: meta
tags: [schema, conventions]
created: 2026-04-09
updated: 2026-04-09
sources: []
---

# Hermes.CS Wiki Schema

Domain: Hermes.CS -- Native Windows AI Agent built in C# / WinUI 3.
Ported from NousResearch/hermes-agent (Python) with native desktop extensions.

## Tag Taxonomy

| Tag | Meaning |
|-----|---------|
| agent | Core agent loop, ChatAsync, StreamChatAsync |
| context | PromptBuilder, TokenBudget, SessionState, ContextManager |
| llm | IChatClient implementations, provider code |
| tools | ITool interface, individual tools, parallel execution |
| soul | Identity, USER.md, SOUL.md, mistakes/habits journals |
| skills | SkillManager, skill loading, invocation |
| memory | MemoryManager, relevance filtering, freshness |
| transcript | TranscriptStore, JSONL persistence, activity log |
| settings | HermesEnvironment, config.yaml, UI settings |
| gateway | GatewayService, platform adapters, messaging |
| security | SecretScanner, permissions, validators |
| streaming | StreamEvent hierarchy, SSE parsing |
| compaction | Context compression, cooldown, orphan sanitization |
| credentials | CredentialPool, rotation, fallback |
| desktop | WinUI 3 app, HermesChatService, views |

## Page Types

- **system** -- Major subsystem (agent-loop, context-management, etc.)
- **entity** -- Key class or interface documentation
- **pattern** -- Reusable implementation pattern
- **concept** -- Architectural decision, history, or analysis

## Page Template

```markdown
---
title: Page Title
type: system|entity|concept|pattern
tags: [tag1, tag2]
created: YYYY-MM-DD
updated: YYYY-MM-DD
sources: [relative/path/to/file.cs]
---

# Page Title

[Dense content, 30-80 lines]

## Key Files
- `path/to/file.cs` -- what it does

## See Also
- [[related-page]]
```

## Conventions

- Paths are relative to repo root (`src/Core/agent.cs`)
- Method signatures use C# syntax
- INV-NNN references are forensic invariant IDs from the upstream analysis
- All line counts and method names verified against actual source
