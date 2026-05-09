# Reference Repo Lessons: OpenManus and DeepSeek-TUI

This note captures the useful patterns from two local reference repositories:

- `artifacts/reference-repos/OpenManus-main`
- `artifacts/reference-repos/DeepSeek-TUI-main`

The goal is not to clone either project. Hermes Desktop already has stronger Windows-native safety, provider fallback, memory/wiki, credential rotation, MCP wrapping, and transcript persistence than OpenManus, and it should stay a native desktop app rather than become a terminal interface. The useful path is selective adaptation: keep Hermes' safety and identity stack, then borrow the sharper runtime, planning, browser, streaming, and UX structures where they improve the desktop.

Both reference projects are MIT licensed. Hermes can use their patterns and port code directly when it maps cleanly, with attribution preserved for substantially derived pieces. The default approach should be "port, adapt, and test" rather than re-inventing equivalent machinery.

## Current Hermes Baseline

Hermes is already a serious in-process agent runtime:

- `src/Core/Agent.cs` owns tool loops, provider fallback, permission gating, activity logging, parallel safe-tool execution, transient system message cleanup, and streaming watchdogs.
- `src/LLM/*` covers provider abstraction, model routing, OpenAI-compatible clients, Anthropic, credential pool rotation, and structured provider errors.
- `src/Tools/*` already includes shell, browser, file, patch, grep/glob, LSP, session search, MCP wrappers, memory, image, vision, web, and automation tools.
- `src/transcript/TranscriptStore.cs` uses crash-aware JSONL persistence with write-through appends.
- `src/tasks/TaskManager.cs` has durable task objects, but today they are closer to project/task metadata than executable background work records.
- `Desktop/HermesDesktop/Views/ChatPage.xaml.cs` and `Desktop/HermesDesktop/Services/HermesChatService.cs` carry too much UI/runtime orchestration compared with the cleaner event boundary in DeepSeek-TUI.

## OpenManus: Useful Ideas

OpenManus is less hardened than Hermes, but it has a clean conceptual separation that is worth borrowing.

### 1. Smaller Agent Loop Phases

OpenManus splits the loop into `BaseAgent.run()`, `ReActAgent.step()`, `ToolCallAgent.think()`, and `ToolCallAgent.act()`:

- `app/agent/base.py`
- `app/agent/react.py`
- `app/agent/toolcall.py`

Hermes' `Agent` is more capable, but it remains monolithic. Future work should keep extracting these phases into C# services:

- context preparation
- model/provider call
- stream accumulation
- tool-call normalization
- tool dispatch and permission gating
- result persistence
- lifecycle cleanup

This would make the agent easier to test and reduce the chance that fixes in provider behavior, context injection, and tool execution collide.

### 2. First-Class Planning Tool

OpenManus' `PlanningFlow` plus `PlanningTool` provide a concrete status model:

- `not_started`
- `in_progress`
- `completed`
- `blocked`

Relevant files:

- `app/flow/planning.py`
- `app/tool/planning.py`

Hermes has `TodoWriteTool` and `TaskManager`, but not an agent-visible plan object with step statuses and notes. Add a `planning` or `plan_update` tool backed by session state or `TaskManager`, then teach the loop to execute the current step only and mark progress. This should become the default visible structure for complex turns and sub-agent work.

### 3. Richer Tool Result Shape

OpenManus' `ToolResult` carries:

- `output`
- `error`
- `base64_image`
- `system`

Relevant file:

- `app/tool/base.py`

Hermes `ToolResult` mostly exposes success/content/error. Extend it with optional metadata rather than breaking existing tools:

- `MimeType`
- `MediaBase64` or artifact reference
- `SystemNote`
- `IsTruncated`
- `RawArtifactPath`
- `Summary`
- `Diagnostics`

This helps browser screenshots, chart outputs, long command logs, and hidden tool-system guidance without dumping everything into the user-visible transcript.

### 4. Browser State Feedback

OpenManus' browser agent gives the model explicit browser state: current URL, tabs, scroll position, interactive elements, and screenshot/context.

Relevant files:

- `app/agent/browser.py`
- `app/tool/browser_use_tool.py`

Hermes' `BrowserTool` is safer and uses Playwright, but its snapshot path is mostly text extraction. Add a `browser_state` action that returns:

- title and URL
- tab list
- scroll position
- viewport dimensions
- clickable/input element refs
- optional screenshot artifact
- last navigation and network failure summary

Then feed that state into the next tool observation or multimodal message for models with vision support.

### 5. Duplicate/Stuck Loop Guard

OpenManus detects repeated assistant messages and injects a strategy-change prompt. Hermes has max iteration limits and a compression cooldown, but it would benefit from finer-grained loop signatures:

- repeated assistant content
- repeated identical tool name + arguments
- repeated tool failure class
- no progress after N steps

Add this to `Agent` as a guardrail before the max tool-iteration fallback.

### 6. Undoable File Edits

OpenManus' string-replace editor has edit history and undo. Hermes' edit/patch tools are safer and more C#-appropriate, but a per-session edit history plus `undo_edit` would be a meaningful safety feature for desktop users.

## DeepSeek-TUI: Useful Ideas

DeepSeek-TUI's best contribution is not the terminal UI. It is the durable, evented runtime model under the UI.

### 1. UI/Engine Event Boundary

DeepSeek separates user intent from engine events:

- `crates/tui/src/core/ops.rs`
- `crates/tui/src/core/events.rs`
- `crates/tui/src/tui/ui.rs`

Hermes should introduce a desktop runtime event layer between `HermesChatService` and `ChatPage.xaml.cs`.

Target shape:

- `ChatRuntimeCommand`: user sent message, cancel, retry, switch model, approve tool, deny tool, steer, resume.
- `ChatRuntimeEvent`: token delta, reasoning delta, tool started, tool updated, tool completed, approval requested, provider switched, error, turn completed.
- `ChatPage` renders events and dispatches commands; it should not know the details of tool loop sequencing.

This also makes tests easier because the desktop shell can be tested as an event reducer instead of UI callback soup.

### 2. Streaming Accumulator

DeepSeek has explicit streaming chunking, commit ticks, line buffering, and low-motion behavior:

- `crates/tui/src/tui/streaming/chunking.rs`
- `crates/tui/src/tui/streaming/commit_tick.rs`
- `crates/tui/src/tui/streaming/line_buffer.rs`
- `crates/tui/src/core/engine/streaming.rs`

Hermes currently mutates chat message content on each token. Add a `StreamingTextAccumulator` for WinUI that:

- batches token updates on a timer
- preserves grapheme boundaries
- flushes immediately on tool boundary/error/turn completion
- tracks revision numbers
- supports reduced-motion or low-frequency updates

This should reduce binding churn and make long responses feel steadier.

### 3. Active Turn Model

DeepSeek keeps in-flight work in an active cell, then finalizes it. Hermes logs activities, but chat bubbles and tool cards are not yet one coherent live turn model.

Add `ActiveTurnViewModel` with:

- assistant text
- reasoning text
- grouped tool cards
- permission prompts
- provider status
- retry/switch actions
- finalization into transcript records

This is especially important for parallel tools and sub-agents, where interleaved events can otherwise feel jumpy.

### 4. Command Registry and Palette

DeepSeek treats commands and keybindings as a discoverable contract:

- `docs/KEYBINDINGS.md`
- `crates/tui/src/commands/mod.rs`
- `crates/tui/src/tui/slash_menu.rs`
- `crates/tui/src/tui/command_palette.rs`

Hermes has ad hoc slash command handling. Add a `CommandRegistry` service with:

- name
- aliases
- description
- usage
- argument parser
- handler
- command category
- keybinding, when applicable

Use it for slash commands, command palette search, help display, diagnostics commands, session actions, settings actions, and future tool invocations.

Good first commands:

- `/help`
- `/new`
- `/resume`
- `/model`
- `/provider`
- `/mcp`
- `/memory`
- `/sessions`
- `/tasks`
- `/diagnostics`
- `/config`

Good first desktop keybindings:

- `Ctrl+K` or `Ctrl+Shift+P`: command palette
- `Esc`: close modal/cancel pending UI state
- `Ctrl+R`: resume session picker
- `Ctrl+L`: clear/refresh transcript view
- `Ctrl+S`: stash current draft

### 5. Durable Thread/Turn/Item Timeline

DeepSeek's runtime model persists:

- thread records
- turn records
- turn item records
- event streams
- schema versions
- replayable SSE events

Relevant file:

- `crates/tui/src/runtime_threads.rs`

Hermes transcripts are crash-safe but message-centric. Add a runtime timeline layer on top:

- `ThreadRecord`: session metadata, model, workspace, mode, title, archived flag.
- `TurnRecord`: status, input summary, timestamps, duration, usage, error.
- `TurnItemRecord`: user message, assistant message, reasoning, tool call, file change, command execution, context compaction, status, error.
- `RuntimeEventRecord`: monotonic sequence for replay, diagnostics, and UI recovery.

This would make session resume, UI restoration, background tasks, and debugging much cleaner.

### 6. Background Tasks With Evidence

DeepSeek's `TaskManager` is executable and evidence-oriented:

- bounded workers
- queued/running/completed/failed/canceled states
- runtime thread/turn linkage
- timeline entries
- tool call summaries
- artifacts
- verification gates
- PR attempts

Relevant file:

- `crates/tui/src/task_manager.rs`

Hermes `TaskManager` tracks tasks and dependencies, but not execution evidence. Evolve it toward:

- task queue and worker pool
- linked session/turn IDs
- artifact records for large logs
- verification gates with command, cwd, exit code, duration, summary, and log path
- checklist progress
- cancellation

This can power long-running desktop work without freezing the chat.

### 7. Structured Recoverable Errors

DeepSeek sends typed errors to the UI via an error taxonomy:

- category
- severity
- recoverability
- code
- message

Relevant file:

- `crates/tui/src/error_taxonomy.rs`

Hermes already has provider errors, but `HermesChatService` should surface structured stream errors to the desktop UI:

- `Code`
- `Provider`
- `Retryable`
- `SuggestedAction`
- `Severity`
- `RawDetail`

Map these to banner actions:

- Retry
- Switch model
- Open settings
- Check provider
- Resume queued draft

### 8. Large Output Routing

DeepSeek avoids polluting the parent context with huge tool output:

- estimates output tokens
- routes large output through synthesis
- stores raw output out-of-band
- returns compact provenance plus summary

Relevant file:

- `crates/tui/src/tools/large_output_router.rs`

Hermes should add a `LargeToolOutputRouter` before tool results are appended to session messages:

- default threshold around 4K estimated tokens
- per-tool thresholds
- raw artifact path
- optional summarization
- `retrieve_tool_result` or `promote_to_context` follow-up tool

This is a high-value context hygiene feature.

### 9. Post-Edit Diagnostics

DeepSeek runs LSP diagnostics after edit/write/patch tools and injects pending diagnostics before the next model call:

- `crates/tui/src/core/engine/lsp_hooks.rs`
- `crates/tui/src/lsp/*`

Hermes has `LspTool`, but diagnostics are agent-pull rather than loop-pushed. Add a post-edit hook:

- after successful `edit_file`, `write_file`, or `patch`
- collect diagnostics for changed files
- attach diagnostics to the active turn
- inject a synthetic user/tool message before the next model request

This makes the model repair compile/type errors immediately.

### 10. Config Layering and Effective Config

DeepSeek has richer config layering: global config, profiles, env overrides, project overlays, provider sub-tables, custom headers, and managed requirements.

Hermes should keep `%LOCALAPPDATA%\hermes\config.yaml` but move toward:

- typed config loading
- validation with actionable errors
- environment override reporting
- workspace-level overlays
- visible effective-config diagnostics view
- provider capability metadata in `ModelCatalog`

## Do Not Copy Blindly

Avoid these direct imports:

- OpenManus' permissive Python execution model.
- OpenManus' shared in-memory `PlanningTool.plans` state as-is.
- OpenManus browser defaults where security is weaker than Hermes' current SSRF checks.
- DeepSeek-specific terminal UI details that do not map to WinUI.
- DeepSeek's Rust storage shape verbatim; port the concepts, not the file layout.

## Recommended Hermes Roadmap

### Phase 1: Desktop Runtime Boundary

1. Add `ChatRuntimeCommand` and `ChatRuntimeEvent`.
2. Move stream/tool/session event shaping out of `ChatPage.xaml.cs`.
3. Add tests for event ordering, cancellation, retry, and error banners.

### Phase 2: Streaming and Active Turn UX

1. Add `StreamingTextAccumulator`.
2. Add `ActiveTurnViewModel`.
3. Group tool calls, reasoning, assistant text, and errors under one active turn.
4. Batch WinUI updates.

### Phase 3: Command Registry

1. Add `CommandRegistry`.
2. Port current slash commands into registry handlers.
3. Add command palette UI and centralized keybindings.
4. Generate help from command metadata.

### Phase 4: Durable Timeline

1. Add thread/turn/item records alongside existing JSONL transcripts.
2. Add schema versioning and metadata-only session list.
3. Add in-flight checkpoint records and draft queue persistence.

### Phase 5: Agent Intelligence Guardrails

1. Add stuck-loop signature detection.
2. Add planning tool with statuses and notes.
3. Add large-output router with artifact storage.
4. Add post-edit LSP diagnostic injection.

### Phase 6: Browser and Tool Results

1. Extend `ToolResult` metadata.
2. Add browser state snapshots with refs and optional screenshots.
3. Add undoable edit history.
4. Add runtime MCP connect/disconnect UI and activity events.

## Highest-Value First Patch

The best first implementation target is the command/runtime boundary, not a new agent feature. It unlocks cleaner streaming, testable UI behavior, command palette work, structured errors, active turns, and durable timelines without destabilizing the existing provider/tool system.

Small first slice:

1. Create `ChatRuntimeEvent` and `ChatRuntimeCommand` models.
2. Add a `StreamingTextAccumulator`.
3. Adapt `HermesChatService.StreamStructuredAsync` to emit richer events internally.
4. Keep `ChatPage.xaml.cs` behavior visually the same, but make it consume the new event stream.
5. Add unit tests for token batching and error event mapping.

## Port Progress

Implemented in the first pass:

- `StreamingTextAccumulator` for batched WinUI streaming updates, adapted from DeepSeek-TUI's chunk/commit-tick pattern.
- `planning` tool with create/get/list/mark_step/delete and step statuses, adapted from OpenManus' planning tool shape.
- `browser_state`/`state` action on `BrowserTool`, adapted from OpenManus browser-state feedback.
- `LargeToolOutputRouter` at the Hermes agent boundary, adapted from DeepSeek-TUI's large-output routing concept.
- `ChatRuntimeEvent` / `ChatRuntimeCommand` typed runtime seam, adapted from DeepSeek-TUI's engine-event boundary.
- `CommandRegistry<TContext>` for metadata-driven slash commands and future command palette use, adapted from DeepSeek-TUI's command catalog.
- Conservative post-edit diagnostics hook extension point, adapted from DeepSeek-TUI's post-edit diagnostics flow. It is quiet by default until a real diagnostics provider is configured.

Still valuable next ports:

- Durable thread/turn/item timeline and executable background task records.
- Real LSP-backed diagnostics provider wired into the post-edit diagnostics hook.
- Command palette UI over the new command registry.
