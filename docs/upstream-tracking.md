# Upstream Tracking: NousResearch Hermes Agent → Hermes.CS Desktop

## Current Parity
- **Upstream version:** v0.7.0 (2026.4.3)
- **Last synced:** 2026-04-04
- **Upstream repo:** https://github.com/NousResearch/hermes-agent
- **Upstream install path:** `%LOCALAPPDATA%\hermes\hermes-agent`

## Architecture Mapping

### Python Module → C# Equivalent

| Upstream (Python) | Hermes.CS (C#) | Status |
|---|---|---|
| `agent/prompt_builder.py` | `src/Context/PromptBuilder.cs` | ✅ Ported + enhanced (cache-safe layers) |
| `agent/context_compressor.py` | `src/Context/ContextManager.cs` + `TokenBudget.cs` | ✅ Ported + enhanced (budget pressure) |
| `agent/memory_manager.py` | `src/memory/memorymanager.cs` | ✅ Ported |
| `agent/memory_provider.py` | — | ❌ Not yet (pluggable provider ABC) |
| `agent/credential_pool.py` | — | ❌ Not yet (multi-key rotation) |
| `agent/anthropic_adapter.py` | `src/LLM/AnthropicClient.cs` | ✅ Ported (streaming, no tool calling yet) |
| `agent/smart_model_routing.py` | — | ❌ Not yet |
| `agent/insights.py` | — | ❌ Not yet (usage/cost tracking) |
| `agent/skill_commands.py` | `src/skills/skillmanager.cs` | ✅ Ported |
| `agent/trajectory.py` | `src/transcript/transcriptstore.cs` | ✅ Ported |
| `agent/title_generator.py` | — | ❌ Not yet |
| `agent/redact.py` | — | ❌ Not yet (secret redaction) |
| `cli.py` + `hermes_cli/` | Desktop UI (WinUI 3) | ✅ Reimplemented as native desktop |
| `run_agent.py` | `src/Core/agent.cs` | ✅ Ported |
| `tools/terminal_tool.py` | `src/Tools/bashtool.cs` | ✅ Ported |
| `tools/file_tools.py` | `src/Tools/readfiletool.cs` + `writefiletool.cs` + `editfiletool.cs` | ✅ Ported |
| `tools/web_tools.py` | `src/Tools/webfetchtool.cs` + `websearchtool.cs` | ✅ Ported |
| `tools/browser_tool.py` | `src/Tools/lsptool.cs` (partial) | 🟡 Different approach |
| `tools/browser_camofox.py` | — | ❌ Not yet (Camofox anti-detection) |
| `tools/memory_tool.py` | `src/memory/memorymanager.cs` | ✅ Ported |
| `tools/mcp_tool.py` | `src/mcp/McpManager.cs` + transports | ✅ Ported |
| `tools/todo_tool.py` | `src/Tools/todowritetool.cs` | ✅ Ported |
| `tools/skills_tool.py` | `src/skills/skillmanager.cs` | ✅ Ported |
| `tools/delegate_tool.py` | `src/Tools/agenttool.cs` | ✅ Ported |
| `tools/approval.py` | `src/permissions/permissionmanager.cs` | ✅ Ported |
| `tools/send_message_tool.py` | — | ❌ Not yet |
| `tools/vision_tools.py` | — | ❌ Not yet |
| `tools/image_generation_tool.py` | — | ❌ Not yet |
| `tools/tts_tool.py` | — | ❌ Not yet |
| `tools/voice_mode.py` | — | ❌ Not yet |
| `tools/code_execution_tool.py` | — | ❌ Not yet |
| `tools/tirith_security.py` | `src/security/ShellSecurityAnalyzer.cs` | ✅ Ported |
| `tools/url_safety.py` | `src/Tools/webfetchtool.cs` (SsrfGuard) | ✅ Ported |
| `gateway/` | `Desktop/HermesDesktop/Views/IntegrationsPage.xaml` | 🟡 Display only (no gateway daemon) |
| `cron/` | `src/Tools/schedulecrontool.cs` | ✅ Ported |
| `environments/` | — | ❌ Not yet (benchmark/eval envs) |
| `acp_adapter/` | — | ❌ Not yet (editor integration) |
| `honcho_integration/` | — | ❌ Not yet |
| `agent/display.py` | Desktop UI controls | ✅ Reimplemented as WinUI |
| `hermes_cli/skin_engine.py` | `App.xaml` theme system | ✅ Reimplemented as XAML resources |
| `hermes_cli/banner.py` | `DashboardPage.xaml` hero section | ✅ Reimplemented |
| `src/buddy/buddy.cs` | `src/buddy/buddy.cs` | ✅ C# original (not in upstream) |
| `src/coordinator/` | `src/coordinator/coordinatorservice.cs` | ✅ C# original (not in upstream) |
| `src/agents/agentservice.cs` | `src/agents/agentservice.cs` | ✅ C# original (multi-agent teams) |
| `src/hooks/HookSystem.cs` | `src/hooks/HookSystem.cs` | ✅ C# original |
| `src/compaction/` | `src/compaction/CompactionSystem.cs` | ✅ C# original |

## v0.7.0 Feature Gaps (What We're Missing)

### High Priority
1. **Pluggable Memory Providers** — upstream now has ABC-based plugin system
2. **Credential Pool Rotation** — multiple API keys with least_used + 401 failover
3. **Inline Diff Previews** — file write/patch show diffs in tool feed
4. **Secret Exfiltration Blocking** — scan URLs/responses for leaked secrets
5. **Stale File Detection** — warn when file modified externally since last read

### Medium Priority
6. **Camofox Browser Backend** — stealth browsing with anti-detection
7. **ACP Editor Integration** — VS Code/Zed/JetBrains MCP server passthrough
8. **API Server Session Continuity** — persistent sessions with X-Hermes-Session-Id
9. **Smart Model Routing** — automatic fallback and per-turn primary restoration
10. **Developer role for GPT-5** — uses OpenAI's recommended system role

### Low Priority (CLI-specific, less relevant to Desktop)
11. `/yolo`, `/btw`, `/profile` slash commands
12. TUI pinning, inline diffs (CLI display)
13. Fork detection in `hermes update`
14. Gateway hardening (we don't run the gateway daemon)

## Update Workflow

When NousResearch ships a new version:

```bash
# 1. Update the CLI
hermes update
hermes --version

# 2. Check what changed
# Read the release notes at:
#   %LOCALAPPDATA%\hermes\hermes-agent\RELEASE_v{VERSION}.md

# 3. Diff the Python source against our mapping table above
# Focus on: agent/, tools/, hermes_cli/config.py, toolsets.py

# 4. Port changes to C# following the mapping table

# 5. Update this file with the new version number and any new mappings
```

## C#-Only Features (Not in Upstream)

These are features unique to Hermes.CS Desktop:
- **Context Runtime** (SessionState, TokenBudget, PromptBuilder, ContextManager)
- **Buddy System** (AI companion with stats)
- **Coordinator Mode** (multi-agent task decomposition)
- **Agent Teams** (AgentRunner, mailbox, SSH isolation)
- **WinUI 3 Desktop UI** (native Windows app)
- **MSIX Packaging** (Windows App SDK deployment)
- **XamlCompiler fix** (UseXamlCompilerExecutable=false)
