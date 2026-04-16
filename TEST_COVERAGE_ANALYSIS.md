# Test Coverage Analysis

> Hermes-Desktop codebase — April 2026

## Executive Summary

The project has **22 test files** with **~479 test methods** covering primarily the Dreamer subsystem and core Agent loop. However, the overall coverage is uneven: the `src/` core library contains **~95 source files** of which only **~18 are directly tested**. Critical subsystems — security validators, execution backends, LLM providers, and the entire tool suite — have little to no test coverage.

The testing documentation (`testing.instructions.md`) sets a goal of **80%+ coverage on business logic** and **100% on utilities**, but no coverage tooling is configured to measure this. The CI pipeline runs smoke tests only — **unit tests are not gated in CI**.

---

## Current Coverage Map

### Well-Tested Areas (Good)

| Area | Test File(s) | Tests | Quality |
|------|-------------|-------|---------|
| Agent core loop | `AgentTests.cs`, `AgentInvariantTests.cs` | ~74 | Comprehensive: tool dispatch, max iterations, permission denials, plugin exceptions, error recovery |
| Dreamer subsystem | 6 files (`BuildSprint`, `DreamerConfig`, `DreamerRoom`, `DreamerStatus`, `EchoDetector`, `SignalScorer`) + 3 service tests | ~144 | Strong: edge cases, concurrency, serialization round-trips |
| Analytics | `InsightsDreamerTests.cs` | ~20 | Good: 50-thread concurrency, save/reload persistence |
| Models | `SessionMessageTests.cs` | ~30 | Solid: property defaults, serialization, HasToolCalls |
| Credential management | `CredentialPoolTests.cs` | Substantial | Lock correctness, pool rotation |
| Markdown parsing | `MarkdownParserLogicTests.cs` | ~58 | Thorough: edge cases in parsing logic |
| Panel helpers | `PanelHelperLogicTests.cs` | ~56 | Thorough: extracted logic from WinUI code-behind |
| Transcript store | `TranscriptStoreTests.cs` | Present | File I/O persistence |
| OpenAI auth | `OpenAiClientAuthTests.cs` | 6 | OAuth proxy, env vars, header mutation safety |

### Partially Tested (Gaps)

| Area | What's Tested | What's Missing |
|------|--------------|----------------|
| `ShellSecurityAnalyzer` | 5 tests: safe read, write disabled, subprocess disabled, sensitive path, path traversal | Pipe chains, command substitution, encoded payloads, chained semicolons, alias bypasses |
| `PermissionManager` | 2 tests: mode getter/setter | Approval flow, denial logic, prompt callback, allow-list caching, per-tool permission checks |
| `HermesChatService` | Logic-only extraction tests | Integration with Agent, error recovery, session lifecycle |

### Not Tested (Critical Gaps)

These source files have **zero direct test coverage**:

#### Tier 1 — Security & Execution (Highest Risk)

| File | Lines | Why It Matters |
|------|-------|----------------|
| `security/SecretScanner.cs` | ~100 | Scans for leaked API keys, tokens, passwords. Regex patterns must not miss real secrets or false-positive on safe strings. |
| `security/CommandNormalizer.cs` | ~70 | Strips safe wrappers (`timeout`, `nohup`, `env`) before security analysis. A bypass here defeats the entire security chain. |
| `security/validators/TokenizedCommandValidator.cs` | ~80 | Token-aware policy enforcement: subprocess detection, file write detection, sensitive path checks. |
| `security/validators/Validators.cs` | Variable | Supporting validation logic for the command security pipeline. |
| `execution/LocalBackend.cs` | ~90 | Executes commands on the host machine. Timeout handling, output capture, background mode. |
| `execution/DockerBackend.cs` | ~80 | Docker container execution. Container lifecycle, volume mounts, network isolation. |
| `execution/SshBackend.cs` | ~80 | Remote SSH execution. Connection handling, key auth, timeout. |
| `execution/ModalBackend.cs` | ~60 | Modal cloud execution backend. |
| `execution/DaytonaBackend.cs` | ~60 | Daytona cloud execution backend. |

#### Tier 2 — LLM & Integration (High Risk)

| File | Lines | Why It Matters |
|------|-------|----------------|
| `LLM/AnthropicClient.cs` | ~200 | Anthropic API client — completely untested while `OpenAiClient` has auth tests. Request construction, streaming, error handling all uncovered. |
| `LLM/ChatClientFactory.cs` | ~50 | Creates the right client for each provider. Wrong client = wrong API calls. |
| `LLM/ModelRouter.cs` | ~80 | Routes requests to appropriate models. Fallback logic, model selection. |
| `LLM/ModelCatalog.cs` | ~60 | Model registry. Missing model lookups, capability matching. |
| `LLM/SwappableChatClient.cs` | ~40 | Hot-swaps LLM provider at runtime. Thread safety, swap-during-request. |
| `mcp/McpManager.cs` | ~100 | Manages MCP server connections. Lifecycle, reconnection, tool discovery. |
| `mcp/McpServer.cs` | ~80 | MCP server protocol implementation. |
| `mcp/McpServerConnection.cs` | ~60 | Individual server connection management. |
| `mcp/McpToolWrapper.cs` | ~50 | Wraps MCP tools for the agent. Parameter translation, error mapping. |
| `mcp/StdioTransport.cs`, `HttpSseTransport.cs`, `WebSocketTransport.cs` | ~200 total | Transport layer — no tests for any transport. |

#### Tier 3 — Business Logic (Medium Risk)

| File | Lines | Why It Matters |
|------|-------|----------------|
| `Context/TokenBudget.cs` | ~80 | Token estimation & budget enforcement. Wrong math = context explosion or premature truncation. |
| `Context/PromptBuilder.cs` | ~100 | Builds system prompts. Ordering, truncation, priority. |
| `Context/ContextManager.cs` | ~80 | Manages conversation context. |
| `Context/SessionState.cs` | ~50 | Session state tracking. |
| `compaction/CompactionSystem.cs` | ~120 | Context compaction with cooldown logic. Compression failures, threshold math. |
| `memory/memorymanager.cs` | ~100 | Long-term memory CRUD operations. Persistence, search, deduplication. |
| `wiki/WikiManager.cs` + 8 files | ~400 total | Knowledge base with FTS5 search. Page CRUD, indexing, search ranking. |
| `soul/SoulService.cs` + 4 files | ~200 total | Agent identity system. Profile loading, registry, extraction. |
| `skills/skillmanager.cs`, `SkillsHub.cs` | ~100 total | Skill registration, invocation, discovery. |
| `tasks/taskmanager.cs` | ~60 | Task tracking and lifecycle. |
| `plugins/BuiltinMemoryPlugin.cs` | ~60 | Memory plugin — only tested indirectly via Agent tests. |
| `agents/agentservice.cs` | ~80 | Agent service management. |
| `briefs/BriefService.cs`, `TaskBrief.cs` | ~80 total | Task brief generation and management. |
| `buddy/buddy.cs` | ~60 | Companion system. |
| `coordinator/coordinatorservice.cs` | ~60 | Multi-agent coordination. |
| `hooks/HookSystem.cs` | ~60 | Event hook system. |
| `dream/autodreamservice.cs` | ~60 | Automatic dreaming triggers. |
| `dreamer/DreamerService.cs` | ~100 | Dreamer orchestration (sub-components tested, but not the service itself). |
| `gateway/GatewayService.cs` | ~80 | Gateway routing to platform adapters. |
| `gateway/platforms/DiscordAdapter.cs` | ~100 | Discord bot integration. |
| `gateway/platforms/TelegramAdapter.cs` | ~100 | Telegram bot integration. |

#### Tier 4 — Tools (Medium Risk, High Volume)

All **27 tool implementations** in `src/Tools/` have **zero direct tests**. They are exercised only indirectly via the Agent loop tests. Individual parameter validation, error handling, and edge cases are uncovered:

`bashtool`, `readfiletool`, `writefiletool`, `editfiletool`, `greptool`, `globtool`, `terminaltool`, `websearchtool`, `webfetchtool`, `browsertool`, `visiontool`, `ttstool`, `transcriptiontool`, `imagegenerationtool`, `codesandboxtool`, `memorytool`, `patchtool`, `checkpointtool`, `agenttool`, `sendmessagetool`, `sessionsearchtool`, `skillinvoketool`, `lsptool`, `homeassistanttool`, `osvtool`, `mixtureofagentstool`, `todowritetool`, `askusertool`, `schedulecrontool`

---

## Infrastructure Gaps

### 1. No Unit Tests in CI

The CI workflow (`ci-smoke-test.yml`) only validates that the app starts without crashing. There is no `dotnet test` step — a failing unit test won't block a PR merge.

### 2. No Code Coverage Tooling

No Coverlet, ReportGenerator, or any coverage tool is configured. Coverage goals (80%/100%) from `testing.instructions.md` are aspirational with no measurement.

### 3. No Integration Tests

The test project only contains unit tests. There are no integration tests that validate multi-component interactions (e.g., Agent -> LLM -> Tool execution -> Permission check).

---

## Recommended Priorities

### Priority 1 — Security Pipeline (Immediate)

The command security chain is the highest-risk untested code. A single bypass could allow arbitrary command execution.

**Proposed tests:**

- **`SecretScannerTests`** — Verify detection of all 20+ prefix patterns (OpenAI, GitHub, AWS, Stripe, etc.), authorization headers, JSON field patterns, private key blocks, DB connection strings. Test false negatives (modified patterns) and false positives (safe strings resembling keys).

- **`CommandNormalizerTests`** — Verify all 11 wrapper patterns are stripped (`timeout`, `nohup`, `nice`, `env`, `strace`, etc.). Test nested wrappers, partial matches, and commands that should NOT be stripped.

- **`TokenizedCommandValidatorTests`** — Verify subprocess detection (bash, python, node), file write detection (rm, mv, cp, chmod), sensitive path blocking (/etc/, /boot/, C:\Windows\). Test command chaining (`;`, `&&`, `||`), pipes, and quoted arguments.

### Priority 2 — Execution Backends

**Proposed tests:**

- **`LocalBackendTests`** — Test timeout enforcement, output/error capture, background mode, working directory handling, cross-platform shell detection. Use mocked `Process` where possible.

- **`DockerBackendTests`** — Test container creation arguments, volume mount construction, timeout handling, cleanup on failure.

### Priority 3 — LLM Provider Parity

The `OpenAiClient` has auth tests but `AnthropicClient` has none. Both handle sensitive credentials and construct API requests.

**Proposed tests:**

- **`AnthropicClientTests`** — Auth header construction, request serialization, streaming response parsing, error mapping (rate limits, auth failures, model not found).

- **`ChatClientFactoryTests`** — Correct client instantiation for each provider string. Unknown provider handling.

- **`ModelRouterTests`** — Model selection logic, fallback behavior, capability matching.

### Priority 4 — Context Management

Token budget math directly impacts user experience (context truncation vs overflow).

**Proposed tests:**

- **`TokenBudgetTests`** — Threshold calculations (75% summary, 94% critical), token estimation accuracy, boundary conditions (0 messages, 1 message, exactly-at-threshold).

- **`CompactionManagerTests`** — Cooldown timing (600s), compaction trigger conditions, summary generation, message trimming order.

- **`PromptBuilderTests`** — System prompt assembly, priority ordering, truncation behavior.

### Priority 5 — Tool Parameter Validation

Each tool should validate its inputs independently rather than relying on the Agent loop.

**Proposed approach:** Start with the most commonly used tools:

- **`BashToolTests`** — Command sanitization, timeout handling, output truncation.
- **`ReadFileToolTests`** — Path validation, line range handling, encoding detection.
- **`EditFileToolTests`** — Unique match verification, whitespace preservation, replace_all behavior.
- **`GrepToolTests`** — Regex compilation, result limiting, binary file handling.
- **`WriteFileToolTests`** — Path validation, overwrite protection, encoding.

### Priority 6 — Wiki & Memory Systems

These manage persistent user data — corruption or loss is high-impact.

**Proposed tests:**

- **`WikiManagerTests`** — Page CRUD, initialization, search indexing, schema validation.
- **`MemoryManagerTests`** — Store/retrieve/search/delete operations, persistence across sessions.

### Priority 7 — Infrastructure Improvements

1. **Add `dotnet test` to CI** — Add a step in `ci-smoke-test.yml` to run `dotnet test` and fail the build on test failures.

2. **Add Coverlet for coverage measurement** — Add `coverlet.collector` to the test project and configure a coverage threshold gate in CI.

3. **Expand `PermissionManager` tests** — The current 2 tests only check mode getter/setter. Add tests for the approval flow, tool-specific permission checks, and the prompt callback mechanism.

---

## Summary Statistics

| Metric | Value |
|--------|-------|
| Source files (src/) | ~95 |
| Source files with direct tests | ~18 (19%) |
| Test files | 22 |
| Test methods | ~479 |
| Security files tested | 1 of 5 (20%) |
| Execution backends tested | 0 of 5 (0%) |
| LLM providers tested | 1 of 8 (12%) |
| Tools tested directly | 0 of 27 (0%) |
| MCP files tested | 0 of 9 (0%) |
| Wiki files tested | 0 of 9 (0%) |
| Unit tests in CI | No |
| Coverage tooling | None |
