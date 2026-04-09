---
title: Hermes.CS Wiki Index
type: meta
tags: [index]
created: 2026-04-09
updated: 2026-04-09
sources: []
---

# Hermes.CS Wiki

Comprehensive reference for the Hermes.CS codebase -- a native Windows AI agent ported from NousResearch/hermes-agent (Python) with WinUI 3 desktop shell.

## Systems

| Page | Summary |
|------|---------|
| [[systems/agent-loop]] | ChatAsync/StreamChatAsync iterative tool loop, permission gating, activity logging |
| [[systems/context-management]] | PromptBuilder 6-layer architecture, TokenBudget, SessionState, compaction |
| [[systems/llm-providers]] | IChatClient, OpenAiClient, AnthropicClient, credential rotation, fallback |
| [[systems/tool-system]] | ITool interface, 27+ tools, parallel execution with 8-worker semaphore |
| [[systems/soul-system]] | SoulService, SOUL.md/USER.md, mistakes.jsonl, habits.jsonl, AutoDreamService |
| [[systems/skill-system]] | SkillManager, YAML frontmatter, SkillInvoker, validation, atomic writes |
| [[systems/memory-system]] | MemoryManager, LLM-based relevance filtering, freshness warnings |
| [[systems/transcript-system]] | TranscriptStore, WriteThrough JSONL, activity log, session resume |
| [[systems/settings-config]] | HermesEnvironment static class, config.yaml parsing, Settings UI |
| [[systems/gateway-integrations]] | GatewayService, Telegram/Discord adapters, 5-tier auth, reconnect backoff |

## Entities

| Page | Summary |
|------|---------|
| [[entities/agent-class]] | Agent.cs constructor, fields, tool loop, permission callback |
| [[entities/chat-client-interface]] | IChatClient 4 methods, ChatResponse, StreamEvent hierarchy |
| [[entities/hermes-environment]] | Static config/path resolution, gateway controls, YAML helpers |

## Patterns

| Page | Summary |
|------|---------|
| [[patterns/compression-cooldown]] | 600s cooldown after compaction failure to prevent death spirals |
| [[patterns/provider-fallback]] | CredentialPool rotation, primary restoration every 5 minutes |
| [[patterns/parallel-tool-execution]] | ParallelSafeTools set, ShouldParallelize, semaphore workers |
| [[patterns/atomic-persistence]] | WriteThrough FileStream, JSONL append, temp-file-rename for skills |

## Concepts

| Page | Summary |
|------|---------|
| [[concepts/version-history]] | v1.1.0 through v1.8.0 changelog |
| [[concepts/forensic-invariants]] | 10 behavioral invariants, implementation status |
| [[concepts/upstream-gap-analysis]] | Parity with NousResearch/hermes-agent Python upstream |
