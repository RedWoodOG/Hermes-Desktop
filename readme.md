# Hermes Desktop Updated

<p align="center">
  <img src="docs/logo.png" alt="Hermes Desktop Logo" width="128" />
</p>

A **Windows-native AI agent** that lives on your desktop. Chat with it, give it tools, let it learn who you are. Built with WinUI 3 and .NET 10.

**v2.5.0** &mdash; [Download](https://github.com/RedWoodOG/Hermes-Desktop/releases/latest) | [Changelog](#changelog) | [Discussion](https://github.com/RedWoodOG/Hermes-Desktop/discussions/10)

---

## Get Started

**Download and run** &mdash; no installer, no SDK, no setup wizard.

1. Grab [`HermesDesktop-portable-x64.zip`](https://github.com/RedWoodOG/Hermes-Desktop/releases/latest) from Releases
2. Extract anywhere
3. Run `HermesDesktop.exe`
4. Add your API key to `%LOCALAPPDATA%\hermes\config.yaml`

Works on Windows 10 (1809+) and Windows 11. The portable build is fully self-contained &mdash; everything ships in the folder.

<details>
<summary>Minimal config.yaml to get chatting</summary>

```yaml
model:
  provider: anthropic
  default: claude-sonnet-4-6
  base_url: https://api.anthropic.com
  api_key: sk-ant-your-key-here

# Add more providers for runtime swapping (optional)
provider_keys:
  anthropic: sk-ant-your-key
  openai: sk-proj-your-key
  ollama_url: http://127.0.0.1:11434/v1
```

First launch creates `%LOCALAPPDATA%\hermes` with config, memory, transcripts, and logs.

</details>

### Updating

**Portable (zip from Releases)** &mdash; your data lives outside the app folder.

1. Quit Hermes Desktop (system tray → Exit, or close the window).
2. Download the latest [`HermesDesktop-portable-x64.zip`](https://github.com/RedWoodOG/Hermes-Desktop/releases/latest).
3. Either **replace the folder** (delete the old extracted folder, extract the new zip to the same path) **or** extract to a new folder and run `HermesDesktop.exe` from there &mdash; either way, **do not delete** `%LOCALAPPDATA%\hermes`; your `config.yaml`, sessions, memory, and wiki stay there.
4. Start the new `HermesDesktop.exe`.

There is no in-app auto-updater yet; check [Releases](https://github.com/RedWoodOG/Hermes-Desktop/releases) when you want a new build.

**Built from git (dev / `dotnet run`)** &mdash; pull and run again:

```powershell
cd Hermes-Desktop
git pull
dotnet run --project Desktop/HermesDesktop/HermesDesktop.csproj -c Debug -p:Platform=x64 --launch-profile "HermesDesktop (Dev)"
```

**MSIX (`run-dev.ps1`)** &mdash; pull, then re-register:

```powershell
cd Hermes-Desktop
git pull
powershell -ExecutionPolicy Bypass -File .\Desktop\HermesDesktop\run-dev.ps1
```

---

## What It Does

Hermes Desktop is an **in-process agent runtime** with a native Windows UI &mdash; not a chat wrapper. The agent runs locally, calls tools, remembers context across sessions, and can reach out to Telegram and Discord on your behalf.

| | |
|---|---|
| ![Chat](docs/screenshots/Screenshot%202026-04-12%20180315.png) | ![Agents](docs/screenshots/Screenshot%202026-04-12%20180348.png) |
| ![Soul Editor](docs/screenshots/Screenshot%202026-04-12%20180428.png) | ![Soul Templates](docs/screenshots/Screenshot%202026-04-12%20180445.png) |
| ![Skills](docs/screenshots/Screenshot%202026-04-12%20180517.png) | ![Memory](docs/screenshots/Screenshot%202026-04-12%20180529.png) |
| ![Integrations](docs/screenshots/Screenshot%202026-04-12%20180602.png) | ![Settings](docs/screenshots/Screenshot%202026-04-12%20180629.png) |

### Agent Runtime

- **27+ tools** &mdash; file ops, shell, web fetch/search, code sandbox, browser automation, vision, TTS, and more
- **Parallel execution** for read-only tools (8-worker semaphore), sequential with permission gating for mutations
- **Runtime model swapping** &mdash; switch between Claude, GPT, Ollama, Qwen, DeepSeek, and others mid-conversation without restarting
- **Sub-agent spawning** with 5 profiles for delegation and parallel work
- **94 skills** across 28 categories (code review, TDD, GitHub workflows, MLOps, research, creative, and more)

### Memory & Identity

- **Soul system** &mdash; persistent personality (SOUL.md), user profile (USER.md), project rules (AGENTS.md), mistakes journal, habits journal
- **12 soul templates** &mdash; Default, Creative, Teacher, Researcher, Pair Programmer, DevOps, Security, and more
- **Wiki knowledge base** &mdash; markdown files with SQLite FTS5 full-text search, Obsidian-compatible, crash-safe writes
- **Compiled memory stack** &mdash; wiki content injected into agent context automatically, configurable in `config.yaml`
- **6-layer context runtime** &mdash; soul context, system prompt, session state, retrieved knowledge, recent turns, current message

### Production Hardening

Built from lessons across 168+ upstream PRs and 46+ production incidents:

- Streaming watchdog surfaces stalled providers within 30 seconds instead of leaving chat hung
- Structured provider error codes for timeouts, auth failures, rate limits, transport failures, and malformed stream chunks
- Chat error banner with Retry and Switch Model actions when streaming fails
- Compression cooldown (600s) to prevent infinite token-burning loops
- Provider fallback with automatic 5-minute restoration
- Credential pool rotation on 401/429
- Atomic writes (WriteThrough + FlushAsync) for crash safety
- Secret scanning on all tool outputs
- Deterministic tool-call IDs for prompt cache efficiency

### Desktop App

Eight pages: **Dashboard** (usage insights, KPIs, platform badges), **Chat** (tool calling, reasoning display, model switcher, side panels), **Agent** (identity editor, souls browser), **Skills** (searchable library with categories), **Memory** (browser + project rules editor), **Buddy** (companion with ASCII art), **Integrations** (Telegram, Discord, and more), **Settings** (model, memory, display, execution, paths).

### Messaging

Native C# adapters for **Telegram** and **Discord** &mdash; no Python CLI required. **Slack, WhatsApp, Matrix, and webhooks** are configured in the same `config.yaml` from Integrations; use the optional **Python gateway** when you want those bots live (not required just to save tokens).

---

## Build from Source

For contributors or anyone who wants to hack on the code.

**Requirements:** Windows 10+, [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), [Windows App SDK 1.7](https://learn.microsoft.com/windows/apps/windows-app-sdk/)

### Dev mode (recommended)

```powershell
git clone https://github.com/RedWoodOG/Hermes-Desktop.git
cd Hermes-Desktop
dotnet run --project Desktop/HermesDesktop/HermesDesktop.csproj -c Debug -p:Platform=x64 --launch-profile "HermesDesktop (Dev)"
```

Runs unpackaged with no MSIX registration. The Dev profile enables `HERMES_DESKTOP_SHOW_LOCAL_DETAILS` so paths and endpoints are visible in the UI. In Visual Studio or Cursor, select the **HermesDesktop (Dev)** launch profile and press F5.

### Packaged dev loop

```powershell
powershell -ExecutionPolicy Bypass -File .\Desktop\HermesDesktop\run-dev.ps1
```

Builds, registers the MSIX package, and launches. Use `-ShowLocalDetails` to surface paths in the UI.

### Build a portable zip

```powershell
.\scripts\publish-portable.ps1 -Zip
```

Produces `Desktop\HermesDesktop\bin\HermesDesktop-portable-x64.zip` &mdash; self-contained, ready to distribute. For ARM64: add `-Platform ARM64`.

<details>
<summary>Clean uninstall, manual build, troubleshooting</summary>

**Updating from git** is covered above under [Updating](#updating) (portable zip vs `dotnet run` vs `run-dev.ps1`).

**Clean uninstall (MSIX):**

```powershell
Get-AppxPackage *EDC29F63* | Remove-AppxPackage
Remove-Item -Recurse -Force Desktop\HermesDesktop\bin, Desktop\HermesDesktop\obj, src\bin, src\obj -ErrorAction SilentlyContinue
```

To also remove user data: `Remove-Item -Recurse -Force "$env:LOCALAPPDATA\hermes"`

**Manual build (if scripts don't work):**

```powershell
dotnet build Desktop/HermesDesktop/HermesDesktop.csproj -c Debug -p:Platform=x64
cd Desktop\HermesDesktop\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64
Add-AppxPackage -Register AppxManifest.xml
Start-Process "shell:AppsFolder\EDC29F63-281C-4D34-8723-155C8122DEA2_1z32rh13vfry6!App"
```

**Troubleshooting:**

- App window doesn't appear? Remove old packages (`Get-AppxPackage *EDC29F63* | Remove-AppxPackage`), clean `bin/` and `obj/`, rebuild.
- Check crash logs: `%LOCALAPPDATA%\hermes\hermes-cs\logs\desktop-startup.log`
- Check Windows crash reports: `C:\ProgramData\Microsoft\Windows\WER\ReportArchive`
- Close overlay software (MSI Afterburner, RTSS) &mdash; these can interfere with WinUI startup.
- Verify SDK: `dotnet --version` should show `10.x.x`
- Build errors on `BriefService` or `DashboardPage`? See [issue #25](https://github.com/RedWoodOG/Hermes-Desktop/issues/25).
- Use `-p:Platform=x64`, not `AMD64` &mdash; see `Desktop/HermesDesktop/AGENTS.md` for details.

**MSIX signing:** Local cert material (`Desktop/HermesDesktop/packaging/dev-msix.pfx`) must stay out of git. Generate a dev cert with `scripts/new-msix-dev-cert.ps1`.

</details>

---

## Project Structure

```
Hermes.CS/
├── src/                         # Core agent library (Hermes.Core)
│   ├── Core/                    #   Agent loop, models, tool interfaces
│   ├── Tools/                   #   27+ tool implementations
│   ├── LLM/                     #   Provider abstraction, model swapping
│   ├── soul/                    #   Identity system, templates, profiles
│   ├── wiki/                    #   WikiManager, FTS5 search
│   ├── Context/                 #   Prompt builder, token budget
│   ├── dreamer/                 #   Background free-association worker
│   ├── gateway/                 #   Telegram, Discord adapters
│   └── ...                      #   memory, skills, security, plugins, etc.
├── Desktop/HermesDesktop/       # WinUI 3 desktop application
│   ├── Views/                   #   8 pages + side panels
│   ├── Services/                #   Chat bridge, environment, diagnostics
│   └── Strings/                 #   Localization (en-us, zh-cn)
├── skills/                      # 94 skill definitions
├── scripts/                     # Build, publish, install scripts
└── HermesDesktop.slnx
```

## Tech Stack

**.NET 10** / C# 13 &bull; **WinUI 3** (Windows App SDK 1.7, Mica backdrop) &bull; **SQLite** FTS5 &bull; **Playwright** &bull; **System.Text.Json**

## Changelog

| Version | Date | Highlights |
|---------|------|------------|
| **v2.5.0** | 2026-04-27 | **Reliability:** 30s streaming watchdog, structured provider errors (`ProviderTimeout`, `ProviderAuth`, `RateLimit`, `StreamParseError`), OpenAI/Anthropic transport + parse error surfacing. **Chat UX:** visible error banner with Retry and Switch Model actions. **Tests:** stream watchdog regression plus full desktop test suite. Assembly / MSIX manifest **2.5.0.0**. |
| **v2.4.0** | 2026-04-19 | **Buddy:** persist to `buddy/buddy.json`, species hatch UI, LLM-off fallback soul, aligned panel identity. **Integrations:** native Telegram/Discord adapter status fix, clearer optional-Python messaging. **Tests:** `BuddyServiceTests`. Assembly / MSIX manifest **2.4.0.0**. |
| **v2.3.1** | 2026-04-13 | Fix v2.3.0 source zip `DreamerStatusSnapshot.LastLocalDigestHint` compile error, fix portable startup `XamlParseException` on `ReplayPanel` (disable `PublishTrimmed`), add `ReplayPanel` constructor diagnostic capture, refresh readme screenshots |
| v2.3.0 | 2026-04-12 | Portable release (self-contained zip, no MSIX required), compiled memory stack, wiki tool, Dev launch profile, `publish-portable.ps1` |
| v2.2.1 | 2026-04-10 | Fix startup crash on fresh clone, safe file ops, one-click installer |
| v2.2.0 | 2026-04-10 | User Profile section in Settings |
| v2.1.0 | 2026-04-10 | Native C# gateway &mdash; Telegram and Discord without Python CLI |
| v2.0.0 | 2026-04-09 | Runtime model swapping (Claude/OpenAI/Ollama/Qwen mid-conversation) |
| v1.9.0 | 2026-04-09 | Wiki knowledge base with SQLite FTS5 search |
| v1.8.0 | 2026-04-09 | Production hardening: cooldowns, fallback, atomic writes, secret scanning |
| v1.7.0 | 2026-04-09 | Anthropic tool calling |
| v1.5.0 | 2026-04-08 | Parallel tool execution (8 workers) |

<details>
<summary>Earlier releases</summary>

| Version | Date | Highlights |
|---------|------|------------|
| v2.1.1 | 2026-04-10 | Fix skills discovery, model dropdown, memory paths |
| v2.0.1 | 2026-04-09 | Fix dark theme, first-run skill copy, gateway notice |
| v1.9.1 | 2026-04-09 | Agent tool loop tests (207 pass), Chat UX |
| v1.6.0 | 2026-04-09 | Execution backends, plugins, analytics dashboard |
| v1.4.0 | 2026-04-08 | +7 new tools (21 total) |
| v1.3.0 | 2026-04-08 | Chat routes through full Agent pipeline |
| v1.2.0 | 2026-04-08 | Settings page overhaul |
| v1.1.0 | 2026-04-08 | Skills page redesign |

</details>

## Acknowledgments

Built on the [NousResearch Hermes Agent](https://github.com/NousResearch/hermes-agent) architecture. This project exists to show appreciation for the NousResearch team &mdash; please support them and use the product they created.

## License

MIT
