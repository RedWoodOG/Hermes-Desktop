# Upstream tracking: NousResearch Hermes Agent → Hermes Desktop

## How to use this document

This file is **not** a parity checklist. Hermes Desktop is a **WinUI + Hermes.Core** product; upstream is a **Python CLI + gateway + TUI** stack. Use this doc to:

- Record **which upstream release** was last reviewed for *ideas and threats* (security, MCP, orchestration patterns).
- Map **Python modules → C# equivalents** when porting a *specific* behavior is justified (see [HERMES_DESKTOP_FRAMEWORK_STRATEGY.md](internal/HERMES_DESKTOP_FRAMEWORK_STRATEGY.md)).
- List **remaining gaps** that are still true in *this* repo after verifying the codebase — not “missing” because an old row said so.

When upstream ships a new version: skim release notes, then **update the table and “Last reviewed”** here; do not bulk-import their backlog into Hermes issues without a Windows job-to-be-done line.

---

## Current reference (upstream)

| Field | Value |
|-------|--------|
| **Upstream repo** | https://github.com/NousResearch/hermes-agent |
| **Latest release reviewed** | **v0.13.0** (calendar tag `v2026.5.7`, 2026-05-07) — “Tenacity” (Kanban, `/goal`, checkpoints v2, MCP SSE/OAuth hardening, security wave). |
| **Last reviewed for Hermes Desktop mapping** | **2026-05-08** |
| **Upstream install path (optional Python sidecar)** | `%LOCALAPPDATA%\hermes\hermes-agent` |

---

## Architecture mapping (Python → C#)

Paths below are under the Hermes Desktop repo root. Status reflects the **C# tree as of last review**, not historical porting notes.

| Upstream (Python) | Hermes Desktop (C#) | Status |
|-------------------|---------------------|--------|
| `agent/prompt_builder.py` | `src/Context/PromptBuilder.cs` | Ported + layered context |
| `agent/context_compressor.py` | `src/Context/ContextManager.cs`, `TokenBudget.cs` | Ported + budget pressure |
| `agent/memory_manager.py` | `src/memory/MemoryManager.cs` | Ported |
| `agent/memory_provider.py` | — | Partial — not pluggable ABC like upstream; extension point TBD |
| `agent/credential_pool.py` | `src/LLM/CredentialPool.cs` | Ported (multi-key / rotation path; wire as needed) |
| `agent/anthropic_adapter.py` | `src/LLM/AnthropicClient.cs` | Ported (streaming + tools) |
| `agent/*` run loop | `src/Core/Agent.cs` | Ported (streaming + tool loop + fallback) |
| `agent/redact.py` (concept) | `src/security/SecretScanner.cs` + redaction in `Agent` tool paths | Partial — expand coverage where tools bypass scanner |
| `tools/terminal_tool.py` | `src/Tools/BashTool.cs`, `TerminalTool.cs` | Ported |
| `tools/file_tools.py` | `ReadFileTool.cs`, `WriteFileTool.cs`, `EditFileTool.cs`, `PatchTool.cs` | Ported |
| Stale read warnings | `src/Tools/FileReadTracker.cs` | Ported (staleness helper) |
| `tools/web_tools.py` | `WebFetchTool.cs`, `WebSearchTool.cs` | Ported (`SsrfGuard` etc.) |
| `tools/browser_tool.py` | `BrowserTool.cs`, `LspTool.cs` | Partial / different shape |
| `tools/browser_camofox.py` | — | Not ported (optional stealth backend) |
| `tools/memory_tool.py` | `MemoryTool.cs` + `MemoryManager` | Ported |
| `tools/mcp_tool.py` / MCP host | `src/mcp/McpManager.cs`, `McpBootstrap.cs`, `McpToolWrapper.cs` | Ported — servers from config connect and **tools register** with the agent (see `Desktop/HermesDesktop/App.xaml.cs` bootstrap) |
| `tools/todo_tool.py` | `TodoWriteTool.cs` | Ported |
| `tools/skills_tool.py` | `SkillManager.cs`, `SkillInvokeTool.cs` | Ported |
| `tools/delegate_tool.py` | `AgentTool.cs` | Ported |
| `tools/approval.py` | `PermissionManager.cs` | Ported |
| `tools/send_message_tool.py` | `SendMessageTool.cs` | Ported |
| `tools/vision_tools.py` | `VisionTool.cs` | Ported |
| `tools/image_generation_tool.py` | `ImageGenerationTool.cs` | Ported |
| `tools/tts_tool.py` | `TtsTool.cs` | Ported |
| `tools/voice_mode.py` | `TranscriptionTool.cs` (and related) | Partial — product differs |
| `tools/code_execution_tool.py` | `CodeSandboxTool.cs`, execution backends | Partial |
| `gateway/` | `IntegrationsPage.xaml` + native adapters | Display / native bots — **no Python gateway daemon** in-process |
| `cron/` | `ScheduleCronTool.cs` | Ported |
| `acp_adapter/` | — | Not ported — see roadmap IDE bridge |
| `environments/` | — | Not ported (eval harnesses) |
| `honcho_integration/` | — | Not ported |
| `cli.py` + TUI | WinUI app + `Hermes.Agent` CLI | Reimplemented / different surface |

---

## Outcome gaps still worth tracking (verify in code before filing work)

These are **themes**, not automatic ports. Re-check after each upstream major release.

1. **MCP depth (UX + transports)** — OAuth flows, SSE health/reconnect, server toggles, discovery — see [docs/mcp.md](mcp.md) and roadmap v2.8.
2. **Inline diff / change review in UI** — tool feed and session surfaces (upstream post-write lint / diff culture).
3. **Orchestration UX** — durable task board, handoff, retries (upstream Kanban / `/goal` — echo semantics, not clone).
4. **IDE bridge** — localhost API + editor extension (upstream ACP — contract-heavy; gate before build).
5. **Semantic / vector memory** — optional embeddings + compaction (roadmap v2.9); privacy + rollback required.
6. **Provider plugin surface** — upstream v0.13 “providers as plugins”; Hermes uses factory + config today — evaluate if third-party provider DLLs matter for Windows users.
7. **Post-write validation** — upstream delta-lint after write/patch; Hermes may add format validators or UI warnings.

### Lower priority for Desktop (CLI-first)

Slash/TUI parity (`/yolo`, pinning), fork detection in `hermes update`, full gateway hardening — track upstream for **security advisories** only unless a CVE-class issue maps to Core.

---

## C#-only product features (not upstream)

- Native **WinUI 3** shell (Dashboard, Chat, Agent, Skills, Memory, Buddy, Integrations, Settings).
- **MSIX** + portable zip distribution.
- **Windows Sandbox** execution backend, **Windows UI Automation** tool path.
- **Buddy**, **Coordinator**, **Agent teams** (`AgentService`), hooks, compaction — see repo and [ROADMAP.md](../ROADMAP.md).

---

## Update workflow (maintainers)

1. Open upstream [releases](https://github.com/NousResearch/hermes-agent/releases) and read the latest **user-facing** notes (security, MCP, agent loop).
2. For each bullet, ask: *Does Hermes Desktop need an echo on Windows?* If yes, add/adjust a row above or a roadmap milestone — not a blind port.
3. Bump **Latest release reviewed** and **Last reviewed** dates in this file.
4. Run tests on Core + desktop smoke for any behavior change.
