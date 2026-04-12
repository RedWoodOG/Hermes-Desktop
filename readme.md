# Hermes Desktop

<p align="center">
  <img src="docs/logo.png" alt="Hermes Desktop Logo" width="128" />
</p>

![Hermes Agent Banner](docs/screenshots/banner.png)

**A native Windows agentic framework** built with WinUI 3 and .NET 10 — featuring runtime model swapping, 27+ tools with parallel execution, production hardening, a persistent soul identity system, 94 skills, and a wiki-based knowledge base.

**Current version: v2.2.1** | [Changelog](#changelog) | [Discussion](https://github.com/RedWoodOG/Hermes-Desktop/discussions/10)

## What This Is

Hermes Desktop is a **Windows-native agent runtime and control plane** — not a port of the upstream Hermes TUI or gateway. It focuses on:

- **In-process agent runtime** with tool calling, permissions, and context management
- **Native Windows UX** that upstream Hermes doesn't provide (soul browser, skills library, activity replay, visual settings, user profile)
- **Runtime model swapping** between providers mid-conversation without restart
- **Native Telegram and Discord** — messaging works without the Python CLI (v2.1.0)
- **Production hardening** (compression cooldown, provider fallback, atomic persistence, secret scanning)

Slack, WhatsApp, Matrix, and other platforms are supported via the Python gateway sidecar — the desktop app can configure and launch it from the Integrations page.

## Screenshots

| Dashboard | Chat |
|-----------|------|
| ![Dashboard](docs/screenshots/Screenshot%202026-04-05%20123632.png) | ![Chat](docs/screenshots/Screenshot%202026-04-05%20123637.png) |

| Agent | Skills |
|-------|--------|
| ![Agent](docs/screenshots/Screenshot%202026-04-05%20123642.png) | ![Skills](docs/screenshots/Screenshot%202026-04-05%20123700.png) |

| Memory | Integrations |
|--------|-------------|
| ![Memory](docs/screenshots/Screenshot%202026-04-05%20123706.png) | ![Integrations](docs/screenshots/Screenshot%202026-04-05%20123711.png) |

## Features

### Runtime Model Swapping (v2.0.0)

Switch between providers mid-conversation with one click. No app restart needed.

- **Claude Sonnet 4.6** (Anthropic) — with full tool calling
- **GPT-5.4 / GPT-5.4 Mini** (OpenAI)
- **Ollama** (local models — GLM-4.7 Flash, Gemma 4, Llama 4, etc.)
- **Qwen** (Alibaba)
- **DeepSeek, MiniMax, OpenRouter, Nous** — via settings

Pattern from Claude Code: `ChatClientFactory` creates fresh client per swap, `SwappableChatClient` proxy routes all existing consumers transparently. API keys stored in `config.yaml` `provider_keys` section.

### 27+ Built-in Tools

| Category | Tools |
|----------|-------|
| **File System** | `read_file`, `write_file`, `edit_file`, `glob`, `grep`, `patch` |
| **Shell** | `bash`, `terminal` |
| **Web** | `web_fetch`, `web_search` |
| **Agent** | `agent` (sub-agent spawner with 5 profiles), `ask_user` |
| **Knowledge** | `memory`, `session_search`, `skill_invoke`, `wiki_search` |
| **Code** | `code_sandbox` (Python/JS/C# with timeout isolation) |
| **Safety** | `checkpoint` (filesystem snapshots for rollback) |
| **Tasks** | `todo_write`, `schedule_cron` |
| **Platforms** | `send_message` (gateway integration) |
| **Dev** | `lsp` (Language Server Protocol) |
| **Heavy** | `browser` (Playwright), `vision`, `tts`, `transcription`, `image_generation` |

Tools execute in **parallel** for read-only operations (8-worker semaphore) and **sequentially** with permission gating for mutations.

### Production Hardening (v1.8.0)

Behavioral invariants learned from 168+ upstream PRs and 46+ production incidents:

| Pattern | What It Prevents |
|---------|-----------------|
| **Compression cooldown** (600s) | Infinite token-burning retry loops |
| **Provider fallback** with 5-min restoration | Stuck on expensive fallback provider |
| **Credential pool rotation** on 401/429 | Silent key exhaustion |
| **Atomic writes** (WriteThrough + FlushAsync) | Data loss on crash |
| **Deterministic tool-call IDs** | Prompt cache misses (3-5x cost) |
| **Secret scanning** on all tool outputs | API key exposure in transcripts |
| **Orphan tool-result sanitization** | Corrupted context after compaction |

### Soul Identity System

Persistent identity stack with 6-layer prompt architecture:

- **SOUL.md** — Agent personality, values, working style
- **USER.md** — User expertise, preferences
- **AGENTS.md** — Per-project rules and conventions
- **Mistakes Journal** — Auto-extracted corrections from past errors
- **Habits Journal** — Reinforced good patterns via dream consolidation
- **12 Soul Templates** — Default, Creative, Teacher, Researcher, Minimalist, Pair Programmer, DevOps, Security, Startup CTO, Mentor, Claude, Nous Hermes

### 94 Skills (74 Hermes + 20 Claude Code)

Markdown-based capabilities with YAML frontmatter across 28 categories:

| Category | Count | Examples |
|----------|-------|---------|
| **Claude Code** | 20 | commit, code-review, TDD, pdf, docx, xlsx, pptx, mcp-builder |
| **Software Dev** | 7 | plan, systematic-debugging, writing-plans |
| **GitHub** | 6 | issues, PR workflow, repo management |
| **MLOps** | 8 | training, evaluation, HuggingFace, vector-databases |
| **Research** | 4 | arxiv, blogwatcher, paper-writing |
| **Creative** | 4 | ASCII art, excalidraw, songwriting |
| **28 categories** | 94 | Apple, gaming, email, smart home, red-teaming, and more |

### Desktop Application (8 Pages)

| Page | Description |
|------|-------------|
| **Dashboard** | KPI cards, usage insights (tool calls, cost), platform badges, recent sessions |
| **Chat** | Agent chat with tool calling, reasoning display, model switcher, side panels (Sessions, Files, Tasks, Replay) |
| **Agent** | Identity editor (SOUL.md, USER.md), souls browser (12 templates), agent profiles |
| **Skills** | Searchable library with category chips, color-coded badges, sort, system prompt preview |
| **Memory** | Memory browser with type badges, project rules (AGENTS.md) editor |
| **Buddy** | Companion with deterministic ASCII art, stats, personality |
| **Integrations** | 6 messaging platform token configs + gateway start/stop |
| **Settings** | 9 collapsible sections: User Profile, Model, Agent, Gateway, Memory, Display, Execution, Plugins, Paths |

### Wiki Knowledge Base (v1.9.0)

Persistent, markdown-based knowledge system with SQLite FTS5 full-text search:

- WikiManager facade with CRUD, search, stats
- Obsidian-compatible markdown files (git-trackable)
- Path traversal prevention, WriteThrough crash-safe writes
- Reduces LLM token usage ~15x vs raw code (80-line wiki page vs 800-line source file)

### Context Runtime

6-layer prompt architecture for optimal cache efficiency:

```
Layer 0: Soul Context (identity, user profile, project rules, journals)
Layer 1: Stable System Prompt (cache anchor)
Layer 2: Session State JSON (goal, constraints, decisions, entities)
Layer 3: Retrieved Context (memories, wiki pages)
Layer 4: Recent Conversation Turns (token-budgeted)
Layer 5: Current User Message
```

With `TokenBudget` (8000 max, 6-turn window), `SessionState` tracking, and `CompactionManager` for iterative summarization under pressure.

## Quick Start

### Prerequisites

- Windows 10 (1809+) or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Windows App SDK 1.7](https://learn.microsoft.com/windows/apps/windows-app-sdk/)

### Fresh Install (recommended)

```powershell
git clone https://github.com/RedWoodOG/Hermes-Desktop.git
cd Hermes-Desktop
powershell -ExecutionPolicy Bypass -File .\Desktop\HermesDesktop\run-dev.ps1
```

The `run-dev.ps1` script builds, registers, and launches in one step.

MSIX dev signing note: local certificate material such as `Desktop/HermesDesktop/packaging/dev-msix.pfx` must stay out of git. If you need a local test cert, see `Desktop/HermesDesktop/packaging/README.md` and generate one on your machine with `scripts/new-msix-dev-cert.ps1`.

### Updating

```powershell
cd Hermes-Desktop
git pull
powershell -ExecutionPolicy Bypass -File .\Desktop\HermesDesktop\run-dev.ps1
```

### Clean Uninstall (remove previous version)

If you're having issues or want a completely fresh install, run this **before** building the new version:

```powershell
# 1. Remove ALL registered Hermes Desktop app packages
Get-AppxPackage *EDC29F63* | Remove-AppxPackage

# 2. (Optional) Remove user data and config — only if you want a full reset
# WARNING: This deletes your config, API keys, sessions, and memories
# Remove-Item -Recurse -Force "$env:LOCALAPPDATA\hermes"

# 3. Clean build artifacts
Remove-Item -Recurse -Force Desktop\HermesDesktop\bin -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force Desktop\HermesDesktop\obj -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force src\bin -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force src\obj -ErrorAction SilentlyContinue

# 4. Rebuild and install fresh
powershell -ExecutionPolicy Bypass -File .\Desktop\HermesDesktop\run-dev.ps1
```

To remove **only** the app package without deleting your config and data:

```powershell
Get-AppxPackage *EDC29F63* | Remove-AppxPackage
```

### Manual Build (if run-dev.ps1 doesn't work)

```powershell
dotnet build Desktop/HermesDesktop/HermesDesktop.csproj -c Debug
cd Desktop\HermesDesktop\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64
Add-AppxPackage -Register AppxManifest.xml
Start-Process "shell:AppsFolder\EDC29F63-281C-4D34-8723-155C8122DEA2_1z32rh13vfry6!App"
```

### Troubleshooting

If the app does not show a window after launch:

1. **Remove old packages first:** `Get-AppxPackage *EDC29F63* | Remove-AppxPackage`
2. **Clean build:** Delete `bin/` and `obj/` folders, then rebuild
3. **Check crash log:** `%LOCALAPPDATA%\hermes\hermes-cs\logs\desktop-startup.log`
4. **Check Windows crash reports:** `C:\ProgramData\Microsoft\Windows\WER\ReportArchive`
5. **Close overlay software:** MSI Afterburner, RTSS, and similar overlay/injection tools can interfere with WinUI apps — close them and retry
6. **Verify .NET 10 SDK:** Run `dotnet --version` — should show `10.x.x`

### Configuration

Create `%LOCALAPPDATA%\hermes\config.yaml`:

```yaml
model:
  provider: anthropic
  default: claude-sonnet-4-6
  base_url: https://api.anthropic.com
  api_key: sk-ant-your-key-here
  temperature: "0.7"
  max_tokens: "4096"

# OpenAI-compatible endpoint with OAuth proxy token (uncomment to use):
#   auth_mode: oauth_proxy_command
#   auth_header: Authorization
#   auth_scheme: Bearer
#   auth_token_command: oauth-proxy-helper print-access-token
#
# Auth is applied per outgoing request. Shared HttpClient instances such as the
# Dreamer background clients must not store credentials in DefaultRequestHeaders.

# Keys for runtime model swapping (optional)
provider_keys:
  anthropic: sk-ant-your-key
  openai: sk-proj-your-key
  qwen: sk-your-qwen-key
  ollama_url: http://127.0.0.1:11434/v1

platforms:
  telegram:
    token: ""
  discord:
    token: ""
    require_mention: true
```

## Project Structure

```
HermesDesktop/
├── src/                              # Core agent library (Hermes.Core)
│   ├── Core/                         # Agent loop, models, interfaces
│   ├── Tools/                        # 27+ tool implementations
│   ├── LLM/                          # Provider abstraction, ChatClientFactory,
│   │                                 #   SwappableChatClient, CredentialPool
│   ├── soul/                         # SoulService, SoulRegistry, AgentProfiles
│   ├── wiki/                         # WikiManager, FTS5 search, index, schema
│   ├── Context/                      # PromptBuilder, TokenBudget, ContextManager
│   ├── compaction/                   # CompactionManager (600s cooldown, iterative)
│   ├── memory/                       # MemoryManager, relevance filtering
│   ├── permissions/                  # PermissionManager, 5 modes
│   ├── skills/                       # SkillManager, SkillsHub
│   ├── transcript/                   # TranscriptStore (WriteThrough JSONL)
│   ├── security/                     # SecretScanner, SSRF protection
│   ├── gateway/                      # GatewayService, platform adapters
│   ├── plugins/                      # PluginManager, IPlugin
│   ├── analytics/                    # InsightsService
│   ├── dream/                        # AutoDreamService (consolidation)
│   └── execution/                    # Docker, SSH, Modal, Daytona backends
├── Desktop/HermesDesktop/            # WinUI 3 desktop application
│   ├── Views/                        # 8 pages + side panels
│   ├── Services/                     # HermesChatService, HermesEnvironment
│   ├── Models/                       # ChatMessageItem, view models
│   └── Controls/                     # CodeBlock, PermissionDialog
├── skills/                           # 94 skill definitions (28 categories)
├── docs/internal/                    # Architecture strategy document
└── HermesDesktop.slnx
```

## Changelog

| Version | Date | Changes |
|---------|------|---------|
| **v2.2.1** | 2026-04-10 | Fix startup crash on fresh clone, safe file ops, one-click installer, clean uninstall instructions |
| v2.2.0 | 2026-04-10 | User Profile section in Settings (name, role, working style, project dir) |
| v2.1.1 | 2026-04-10 | Fix skills discovery (dynamic repo root), model dropdown (shows user config first), memory paths |
| v2.1.0 | 2026-04-10 | Native C# gateway — Telegram and Discord work without Python CLI |
| v2.0.1 | 2026-04-09 | Fix dark text theme, first-run skill copy, gateway requirement notice |
| v2.0.0 | 2026-04-09 | Runtime model swapping (Claude/OpenAI/Ollama/Qwen mid-conversation) |
| v1.9.1 | 2026-04-09 | Agent tool loop test (207 pass), Chat UX (tool count, session copy) |
| v1.9.0 | 2026-04-09 | Wiki Phase 1: core data layer with SQLite FTS5 search |
| v1.8.0 | 2026-04-09 | Production hardening: compression cooldown, provider fallback, atomic writes, deterministic IDs |
| v1.7.0 | 2026-04-09 | Anthropic tool calling (Claude can now use all tools) |
| v1.6.0 | 2026-04-09 | Execution backends, plugins, analytics dashboard |
| v1.5.0 | 2026-04-08 | Parallel tool execution (8 workers for read-only tools) |
| v1.4.0 | 2026-04-08 | +7 new tools (21 total), YAML quoting, partial save fix |
| v1.3.0 | 2026-04-08 | Chat routes through full Agent pipeline (tools work in chat) |
| v1.2.0 | 2026-04-08 | Settings page overhaul (8 collapsible sections) |
| v1.1.0 | 2026-04-08 | Skills page redesign (grouped categories, color badges, sort) |

## Tech Stack

- **.NET 10** with C# 13
- **WinUI 3** (Windows App SDK 1.7) with Mica backdrop
- **SQLite** (FTS5 full-text search for wiki)
- **Microsoft.Playwright** (browser automation)
- **System.Text.Json** (serialization)

## Based On

Built on the [NousResearch Hermes Agent](https://github.com/NousResearch/hermes-agent) architecture. Hermes Desktop is a native Windows implementation that brings agentic AI to the desktop with a modern WinUI 3 interface. This project exists to show appreciation for the incredible work by the NousResearch team — please support them and use the product they created.

## License

MIT
