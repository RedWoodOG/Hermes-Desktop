# Hermes Desktop

![Hermes Agent Banner](docs/screenshots/banner.png)

A native Windows desktop AI agent built with WinUI 3 and .NET 10.

## Screenshots

| Chat | Integrations |
|------|-------------|
| ![Chat Interface](docs/screenshots/chat.png) | ![Integrations](docs/screenshots/integrations.png) |

## Features

### 13 Built-in Tools

| Tool | Description |
|------|-------------|
| `bash` | Execute shell commands with timeout and background support |
| `read_file` | Read file contents with offset/limit pagination |
| `write_file` | Create or overwrite files with read-before-write enforcement |
| `edit_file` | Precise string replacement with uniqueness validation |
| `glob` | Fast pattern-based file search sorted by modification time |
| `grep` | Content search powered by ripgrep with regex support |
| `web_search` | Search the web and return structured results |
| `web_fetch` | Fetch and extract content from URLs |
| `agent` | Spawn sub-agents for parallel task execution |
| `todo_write` | Task list management with status tracking |
| `ask_user` | Prompt the user for input or confirmation |
| `schedule_cron` | Schedule recurring tasks with cron expressions |
| `terminal` | Interactive terminal session management |

### 8 LLM Providers

- **Nous** (Hermes models)
- **OpenAI** (GPT-4o, o1, o3)
- **Anthropic** (Claude Sonnet, Opus)
- **Qwen** (Qwen 3.5, QwQ)
- **DeepSeek** (V3, R1)
- **MiniMax** (MiniMax-01)
- **OpenRouter** (any model via router)
- **Local** (Ollama, LM Studio, any OpenAI-compatible endpoint)

### Streaming Chat

- Token-by-token streaming display
- Inline diff previews for file edits
- Markdown rendering in chat bubbles
- Tool use progress indicators

### 6 Messaging Integrations

- Telegram
- Discord
- Slack
- WhatsApp
- Matrix
- Webhook (generic HTTP)

### Agent Capabilities

- **MCP Server Integration** -- connect external tool servers via Model Context Protocol
- **Skills System** -- slash commands with Markdown+YAML skill files
- **Permission System** -- rule DSL with 5 modes (default, plan, auto, bypass, acceptEdits) and a WinUI dialog for user approval
- **Context Runtime** -- persistent memory, token budget management, automatic summarization
- **Security** -- SSRF protection, secret scanning, shell command analysis
- **Credential Pool** -- provider key rotation across multiple API keys
- **Session Persistence** -- JSONL transcript-first storage with crash recovery

## Quick Start

### Prerequisites

- Windows 10 (1809+) or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Windows App SDK 1.7](https://learn.microsoft.com/windows/apps/windows-app-sdk/)

### Build and Run

```bash
# Clone the repo
git clone https://github.com/jwhitlark/Hermes.CS.git
cd Hermes.CS

# Build the core library
cd src
dotnet build Hermes.Core.csproj

# Build and run the desktop app
cd ../Desktop/HermesDesktop
dotnet build
dotnet run
```

## Configuration

Create `%LOCALAPPDATA%\hermes\config.yaml`:

```yaml
llm:
  provider: qwen
  model: qwen3.5
  baseUrl: https://dashscope.aliyuncs.com/compatible-mode/v1
  apiKey: sk-your-key-here

# Or use a local model via Ollama
# llm:
#   provider: openai
#   model: qwen3.5
#   baseUrl: http://localhost:11434/v1
#   apiKey: ""

messaging:
  telegram:
    botToken: ""
  discord:
    botToken: ""

security:
  ssrf:
    enabled: true
  secretScanning:
    enabled: true
```

## Project Structure

```
Hermes.CS/
├── src/                          # Core agent library
│   ├── Core/                     # Base models, interfaces, message types
│   ├── Tools/                    # 13 tool implementations
│   ├── LLM/                     # LLM client abstraction
│   ├── agents/                  # Agent teams, coordination
│   ├── coordinator/             # Multi-agent orchestration
│   ├── memory/                  # Persistent memory system
│   ├── permissions/             # Permission rules and modes
│   ├── skills/                  # Skills system
│   ├── tasks/                   # Task management
│   ├── transcript/              # Session persistence
│   ├── buddy/                   # Companion buddy system
│   ├── dream/                   # Auto-consolidation
│   ├── context/                 # Context runtime (budget, builder)
│   ├── security/                # SSRF, secret scanning, shell analysis
│   └── Hermes.Core.csproj
├── Desktop/
│   └── HermesDesktop/           # WinUI 3 desktop application
│       ├── Models/              # View models
│       ├── Services/            # Chat service, environment
│       ├── Views/               # XAML pages and controls
│       └── HermesDesktop.csproj
├── docs/                        # Architecture documentation
└── Hermes.CS.sln
```

## Tech Stack

- **.NET 10** -- latest .NET runtime
- **WinUI 3** -- Windows App SDK 1.7
- **C# 13** -- modern language features (primary constructors, collection expressions, etc.)
- **System.Text.Json** -- high-performance JSON serialization
- **YamlDotNet** -- configuration parsing

## Based On

This project is based on the [NousResearch Hermes](https://github.com/NousResearch) agent architecture. Hermes Desktop is a native Windows implementation of the Hermes agent design, bringing agentic AI capabilities to the desktop with a modern WinUI 3 interface.

## License

MIT
