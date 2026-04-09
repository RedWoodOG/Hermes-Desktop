---
title: Upstream Gap Analysis
type: concept
tags: [upstream, parity]
created: 2026-04-09
updated: 2026-04-09
sources: [hermes-agent-repo/]
---

# Upstream Gap Analysis

Comparison of Hermes.CS (C#) with NousResearch/hermes-agent (Python upstream).

## Ported and Functional

| Feature | Upstream | C# Implementation |
|---------|----------|-------------------|
| Agent loop with tool calling | agent.py | src/Core/agent.cs - ChatAsync + StreamChatAsync |
| IChatClient abstraction | chat_client.py | src/LLM/ichatclient.cs |
| OpenAI-compatible client | openai_client.py | src/LLM/openaiclient.cs |
| Anthropic client | anthropic_client.py | src/LLM/AnthropicClient.cs |
| Credential pool rotation | credential_pool.py | src/LLM/CredentialPool.cs |
| Tool system (ITool interface) | tools/ | src/Tools/ (27+ tools) |
| Transcript persistence | transcript_store.py | src/transcript/transcriptstore.cs |
| Soul system (SOUL.md, USER.md) | soul/ | src/soul/SoulService.cs |
| Skill system | skills/ | src/skills/skillmanager.cs |
| Memory system | memory/ | src/memory/memorymanager.cs |
| Context management | context/ | src/Context/ |
| Permission system | permissions/ | src/permissions/permissionmanager.cs |
| Secret scanning | redact.py | src/security/SecretScanner.cs |
| Gateway | gateway/ | src/gateway/GatewayService.cs |
| Compaction | compaction/ | src/compaction/CompactionSystem.cs |
| Smart model routing | smart_model_routing.py | src/LLM/ModelRouter.cs |

## Intentionally Different

| Area | Upstream (Python) | C# Approach | Reason |
|------|-------------------|-------------|--------|
| Desktop UI | None (CLI only) | WinUI 3 app | Native Windows experience |
| Streaming architecture | Full SSE streaming for tool calls | Tool calls use CompleteWithToolsAsync, only final text streams | Matches Python agent behavior |
| Config format | YAML with yaml library | Hand-rolled line-by-line parser | No YAML dependency |
| Token counting | tiktoken library | ~4 chars/token heuristic | Avoids native dependency |
| Plugin system | Not in upstream | PluginManager + IPlugin | Extensibility for desktop |
| Execution backends | Local only | Local, Docker, SSH, Daytona, Modal | Multi-environment support |
| Analytics | Not in upstream | InsightsService | Desktop analytics |
| MCP integration | Not in upstream | src/mcp/ (McpManager, transports) | Model Context Protocol |

## Potentially Missing / Partial

| Feature | Status | Notes |
|---------|--------|-------|
| WriteThrough in SoulService | Partial | Mistake/habit journals use plain File.AppendAllTextAsync, not WriteThrough |
| Parallel execution in streaming | Missing | StreamChatAsync runs all tools sequentially, no parallel path |
| Permission gate in parallel path | Missing | Parallel execution skips permission checking |
| ModelCatalog | Present | src/LLM/ModelCatalog.cs exists but not deeply integrated |
| Coordinator service | Present | src/coordinator/coordinatorservice.cs -- multi-agent coordination |
| Brief system | Present | src/briefs/BriefService.cs, TaskBrief.cs -- task briefing |
| Hook system | Present | src/hooks/HookSystem.cs -- lifecycle hooks |
| Buddy system | Present | src/buddy/buddy.cs -- companion agent |

## Architecture Differences

The Python upstream is a CLI-first agent. Hermes.CS adds:
- **Desktop shell** -- WinUI 3 with ChatPage, AgentPage, SettingsPage, MemoryPage, SkillsPage
- **Panel system** -- AgentPanel, BuddyPanel, MemoryPanel, SessionPanel, SkillsPanel, TaskPanel
- **Session recording** -- SessionRecorder for replay
- **File browser** -- FileBrowserPanel for workspace navigation
- **Search index** -- SessionSearchIndex for transcript search

## Key Files
- `hermes-agent-repo/` -- Python upstream (cloned at repo root)
- `src/` -- C# implementation
- `Desktop/HermesDesktop/` -- WinUI 3 desktop app

## See Also
- [[version-history]]
- [[forensic-invariants]]
