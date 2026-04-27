# Hermes Desktop Roadmap

> Last updated: 2026-04-27 | Current version: v2.5.0

This roadmap closes competitive gaps against Superset, Nimbalyst, Codex, Cursor, Claude Code, and Capy while preserving Hermes's unique strengths: native Windows UI, runtime model swapping, soul/identity system, and native messaging.

---

## Competitive Position

| Dimension | Hermes | Leaders | Gap |
|-----------|--------|---------|-----|
| Native Windows UI | WinUI 3 + Mica | Cursor (Electron), Codex (mixed) | Ahead |
| Runtime model swapping | Claude/GPT/Ollama/Qwen/DeepSeek mid-conversation | None do this | Ahead |
| Memory & identity | Soul system, wiki FTS5, 12 templates, compiled memory | None have this depth | Ahead |
| Tool breadth | 27+ tools, parallel read-only (8 workers), permission gating | Comparable to Claude Code | Par |
| Session orchestration | Flat chat list | Superset kanban, Nimbalyst project view | Behind |
| File traceability | Manual `git diff` in worktrees | Nimbalyst inline diff review | Behind |
| IDE integration | Standalone only | Cursor/Codex live in editor | Behind |
| Cloud execution | Interface only (Docker/SSH/Modal/Daytona stubs) | Devin cloud-first, Superset worktrees | Behind |
| MCP ecosystem | Loads config, never registers tools | OpenCode/Superset first-class MCP | Behind |
| Auto-updates | None | All competitors have this | Behind |
| Streaming reliability | Hangs on some providers (Issue #26) | Claude Code/Codex robust | Behind |
| Mobile monitoring | Telegram/Discord bots only | Nimbalyst iOS app | Behind |

---

## Milestones

### v2.5.0 — Reliability & Foundation (2 weeks)

**Theme**: Fix what breaks, ship what users expect.

| Item | Files | Owner | Status |
|------|-------|-------|--------|
| Streaming watchdog � 30s timeout with `StreamEvent.Error` yield | `src/Core/Agent.cs`, `src/LLM/AnthropicClient.cs`, `OpenAiClient.cs` | Codex | Done |
| Transport-layer error surfacing � HTTP, timeout, JSON parse errors | `src/LLM/*Client.cs` | Codex | Done |
| Chat error banner � Retry / Switch Model actions | `Desktop/HermesDesktop/Views/ChatPage.xaml.cs` | Codex | Done |
| Structured error codes � ProviderTimeout, ProviderAuth, RateLimit, StreamParseError | `src/Core/Agent.cs` | Codex | Done |
| Auto-updater — GitHub Releases check, portable zip download, SHA256 verify | `Desktop/HermesDesktop/Services/UpdateService.cs` (new) | — | Planned |
| MSIX update channel wiring | `Desktop/HermesDesktop/packaging/` | — | Planned |
| Settings: "Check for updates" button + "Auto-update" toggle | `Desktop/HermesDesktop/Views/SettingsPage.xaml` | — | Planned |

**Acceptance**:
- Network disconnect mid-stream -> visible error within 35s, not a hang.
- Launch on old version -> "Update available" banner with release notes link.
- Update downloads, verifies, restarts without data loss.

---

### v2.6.0 — Session Orchestration (2 weeks)

**Theme**: Go from chat list to workspace.

| Item | Files | Owner | Status |
|------|-------|-------|--------|
| Task Board page — Kanban: Backlog / Running / Review / Done | `Desktop/HermesDesktop/Views/TaskBoardPage.xaml` (new) | — | Planned |
| AgentTaskItem model — TaskId, Title, Status, Tags, Project, CostTokens | `Desktop/HermesDesktop/Models/AgentTaskItem.cs` (new) | — | Planned |
| AgentService events — AgentTaskStatusChanged on spawn/complete/failure | `src/agents/AgentService.cs` | — | Planned |
| SessionTagStore — SQLite `agent_tasks` table with FTS5 | `Desktop/HermesDesktop/Services/SessionTagStore.cs` (new) | — | Planned |
| Drag-and-drop + filter bar (project, tag, date, status) | `TaskBoardPage.xaml` | — | Planned |
| Changes Review panel — file tree, inline diff, Apply/Discard | `Desktop/HermesDesktop/Views/Panels/ChangesPanel.xaml` (new) | — | Planned |
| DiffRenderer — unified diff parser + syntax highlight | `Desktop/HermesDesktop/Helpers/DiffRenderer.cs` (new) | — | Planned |
| Track ReadFiles/WrittenFiles/DeletedFiles per AgentContext | `src/agents/AgentService.cs` | — | Planned |

**Acceptance**:
- Spawn 3 background agents -> all appear in Running column with live cost counters.
- Drag agent to Review -> status persists after restart.
- Agent modifies file -> ChangesPanel shows diff, Apply lands changes in workspace.

---

### v2.7.0 — IDE Bridge (2 weeks)

**Theme**: Bring Hermes into the editor.

| Item | Files | Owner | Status |
|------|-------|-------|--------|
| VS Code extension scaffold | `extensions/vscode/` (new) | — | Planned |
| Sidebar tree view — Hermes Sessions with live status | `extensions/vscode/src/SessionProvider.ts` | — | Planned |
| Command palette — "Send selection to agent", "Review pending changes" | `extensions/vscode/package.json` | — | Planned |
| Webview chat panel | `extensions/vscode/src/ChatPanel.ts` | — | Planned |
| HermesApiServer — localhost HTTP + WebSocket API | `Desktop/HermesDesktop/Services/HermesApiServer.cs` (new) | — | Planned |
| API endpoints: GET /api/sessions, POST /api/sessions/{id}/chat, GET /api/sessions/{id}/changes | `HermesApiServer.cs` | — | Planned |
| Security: 127.0.0.1 binding, Bearer token from `.api-token` | `HermesApiServer.cs` | — | Planned |

**Acceptance**:
- Open VS Code -> Hermes sidebar shows active sessions.
- Select code -> right-click -> "Send to Hermes" -> appears in session.
- Agent proposes changes -> VS Code gutter shows Accept/Reject.

---

### v2.8.0 — Cloud & MCP (2 weeks)

**Theme**: Extend reach without leaving local-first.

| Item | Files | Owner | Status |
|------|-------|-------|--------|
| DockerBackend — Docker.DotNet, pull, exec, capture, cleanup | `src/execution/DockerBackend.cs` | — | Planned |
| SshBackend — SSH.NET connect, exec, capture | `src/execution/SshBackend.cs` | — | Planned |
| DaytonaBackend — REST API implementation | `src/execution/DaytonaBackend.cs` | — | Planned |
| BashTool routing — ExecutionBackendFactory.Create per config | `src/Tools/BashTool.cs` | — | Planned |
| Settings: "Execution Environment" dropdown + per-tool override | `Desktop/HermesDesktop/Views/SettingsPage.xaml` | — | Planned |
| Permission gate on first Docker/SSH use | `Desktop/HermesDesktop/Services/PermissionDialogService.cs` | — | Planned |
| MCP auto-discovery — `~/.config/mcp/settings.json`, `%LOCALAPPDATA%\hermes\mcp.json` | `Desktop/HermesDesktop/Services/McpDiscoveryService.cs` (new) | — | Planned |
| Settings: MCP server list with toggles + test connection | `SettingsPage.xaml` | — | Planned |
| Wire McpManager into App.xaml.cs tool registration | `Desktop/HermesDesktop/App.xaml.cs` | — | Planned |
| Ship pre-configured MCP servers: filesystem, fetch, git, playwright | `Desktop/HermesDesktop/Services/McpDiscoveryService.cs` | — | Planned |

**Acceptance**:
- Set backend to Docker -> bash tool runs in `ubuntu:24.04` container.
- Add `github-mcp-server` to `mcp.json` -> "github" appears in tool list.
- Toggle off in Settings -> tool disappears without restart.

---

### v2.9.0 — Memory & Monitoring (2 weeks)

**Theme**: Smarter context, broader reach.

| Item | Files | Owner | Status |
|------|-------|-------|--------|
| Telegram `/status` command — running agents, tokens today, last error | `src/gateway/platforms/TelegramAdapter.cs` | — | Planned |
| StatusBroadcaster — push updates to gateways every 60s | `Desktop/HermesDesktop/Services/StatusBroadcaster.cs` (new) | — | Planned |
| Local web dashboard — `localhost:PORT/dashboard`, mobile-responsive | `Desktop/HermesDesktop/Services/HermesApiServer.cs` | — | Planned |
| VectorStore — SQLite + `sqlite-vec` for local embeddings | `src/memory/VectorStore.cs` (new) | — | Planned |
| Semantic compaction — store embeddings of dropped messages | `src/Context/ContextManager.cs` | — | Planned |
| Context injection — query vector store by similarity, inject as system notes | `src/Context/PromptBuilder.cs` | — | Planned |
| Settings: "Semantic compaction" toggle + embedding model selector | `SettingsPage.xaml` | — | Planned |
| Fallback to keyword search if Ollama unavailable | `src/Context/ContextManager.cs` | — | Planned |

**Acceptance**:
- Send `/status` to Telegram -> "3 agents running, 12.4k tokens today".
- Chat 50 turns, compaction drops turns 10-40. Ask about dropped topic -> agent retrieves from vector store and answers.
- Vector store persists across restarts.

---

### v2.10.0 — Polish (1 week)

**Theme**: Finish the experience.

| Item | Files | Owner | Status |
|------|-------|-------|--------|
| Diagnostics page — provider health, recent errors, system info, "Copy diagnostics" | `Desktop/HermesDesktop/Views/DiagnosticsPage.xaml` (new) | — | Planned |
| Startup health check toast — malformed API key, connectivity failure | `Desktop/HermesDesktop/Services/StartupDiagnostics.cs` | — | Planned |
| Settings search — AutoSuggestBox filtering by label/description | `Desktop/HermesDesktop/Views/SettingsPage.xaml` | — | Planned |
| Command palette — `Ctrl+Shift+P` overlay: Switch model, Clear permissions, Open diagnostics, Check for updates | `Desktop/HermesDesktop/MainWindow.xaml.cs` | — | Planned |

**Acceptance**:
- Open Diagnostics -> green/red provider status, last 20 errors, copy-to-clipboard.
- Startup with bad key -> toast: "OpenAI key invalid" before first chat.
- `Ctrl+Shift+P` -> type "model" -> "Switch to Claude" -> executes.

---

## Architecture Principles

1. **Local-first always**: All features work offline. No cloud dependency for core functionality.
2. **Additive only**: Every new feature is toggleable in Settings. Users who prefer v2.4.0 can disable new panels.
3. **Git for state**: Session history, task board, file changes — all in SQLite with atomic writes (existing pattern).
4. **No Electron**: VS Code extension talks to native Hermes via WebSocket. Hermes stays WinUI 3.
5. **Test coverage**: Every new service gets unit tests. Every UI page gets a smoke test for "loads without crash."

---

## Risk Register

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| VS Code extension maintenance burden | Medium | Medium | Start with read-only session list + chat send only |
| Docker backend requires Docker Desktop | High (for users) | Low | Optional. LocalBackend remains default |
| Semantic compaction needs Ollama | Medium | Medium | Fallback to keyword search (`SessionSearchIndex`) |
| Auto-updater breaks on custom builds | Low | High | Only check GitHub Releases if `AssemblyCompany == "RedWoodOG"` |
| Kanban adds UI complexity | Medium | Medium | Separate page, not replacing Chat. Users opt in |
| SQLite-vec extension availability | Medium | Medium | Bundle `sqlite-vec` DLL or use Ollama embeddings |

---

## How to Contribute

- Pick a milestone item, open a draft PR referencing this roadmap.
- Keep changes scoped to one item per PR.
- Add tests for new services, smoke tests for new pages.
- All UI changes must include `zh-cn` localization strings.

---

*This roadmap is a living document. Priorities shift based on user feedback, competitive moves, and contributor capacity. Open a discussion to propose changes.*
