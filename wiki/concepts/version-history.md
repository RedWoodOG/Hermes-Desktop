---
title: Version History
type: concept
tags: [history, changelog]
created: 2026-04-09
updated: 2026-04-09
sources: [readme.md]
---

# Version History

Hermes.CS is a C# port of NousResearch/hermes-agent (Python) with native Windows desktop extensions.

## v1.1.0 -- Foundation

- Core Agent class with ChatAsync
- ITool interface and basic tool registration
- OpenAiClient for OpenAI-compatible endpoints
- TranscriptStore with JSONL persistence
- Basic CLI entry point (src/Program.cs)

## v1.2.0 -- Context System

- PromptBuilder with 6-layer prompt architecture
- TokenBudget with pressure levels (Normal/High/Critical)
- SessionState structured rolling memory
- ContextManager orchestrating context preparation
- Cache-safe prompt construction for provider KV reuse

## v1.3.0 -- Soul & Memory

- SoulService with SOUL.md, USER.md, AGENTS.md
- Mistake and habit journals (JSONL append-only)
- MemoryManager with LLM-based relevance filtering
- AssembleSoulContextAsync for prompt injection
- Default soul template with agent identity

## v1.4.0 -- Skills & Tools Expansion

- SkillManager with YAML frontmatter parsing
- SkillInvoker for one-shot skill execution
- Expanded tool set: browser, web_fetch, web_search, vision, TTS, transcription
- Permission system with Allow/Deny/Ask behaviors

## v1.5.0 -- Streaming & Desktop

- StreamChatAsync with IAsyncEnumerable<StreamEvent>
- AnthropicClient with Anthropic Messages API
- StreamEvent hierarchy (TokenDelta, ThinkingDelta, ToolUse*, MessageComplete, StreamError)
- WinUI 3 desktop app shell (HermesDesktop)
- HermesChatService bridging agent to UI

## v1.6.0 -- Execution Backends, Plugins, Analytics

- IExecutionBackend interface with Local, Docker, SSH, Daytona, Modal backends
- PluginManager with IPlugin interface
- BuiltinMemoryPlugin bridging MemoryManager to plugin system
- InsightsService for analytics
- Wired execution backends into agent pipeline

## v1.7.0 -- Gateway & Integrations

- GatewayService multi-platform messaging hub
- IPlatformAdapter interface
- TelegramAdapter and DiscordAdapter
- 5-tier authorization system
- Session routing and stale agent detection
- Exponential backoff reconnection (30s -> 300s cap)

## v1.8.0 -- Resilience & Compaction

- CredentialPool with multi-key rotation (LeastUsed, RoundRobin, Random, FillFirst)
- Provider fallback state machine (primary -> fallback -> restoration every 5 min)
- CompactionManager with 600s cooldown pattern
- Orphan tool-result sanitization after compaction
- SecretScanner with 20+ API key prefix patterns
- Iterative summarization (update existing summary vs regenerate)
- Parallel tool execution with 8-worker semaphore
- Deterministic tool-call ID normalization
- ModelRouter for smart cost-saving routing

## Key Files
- `readme.md` -- project README

## See Also
- [[forensic-invariants]]
- [[upstream-gap-analysis]]
