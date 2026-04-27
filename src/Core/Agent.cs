namespace Hermes.Agent.Core;

using Hermes.Agent.LLM;
using Hermes.Agent.Permissions;
using Hermes.Agent.Plugins;
using Hermes.Agent.Security;
using Hermes.Agent.Soul;
using Hermes.Agent.Transcript;
using Hermes.Agent.Memory;
using Hermes.Agent.Context;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;

public sealed class Agent : IAgent
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<Agent> _logger;
    private readonly Dictionary<string, ITool> _tools = new();

    // Optional subsystem dependencies — Agent works without any of these
    private readonly PermissionManager? _permissions;
    private readonly TranscriptStore? _transcripts;
    private readonly MemoryManager? _memories;
    private readonly ContextManager? _contextManager;
    private readonly SoulService? _soulService;
    private readonly PluginManager? _pluginManager;

    // INV-004/005: Provider fallback state machine
    private readonly IChatClient? _fallbackChatClient;
    private readonly CredentialPool? _credentialPool;
    private bool _usingFallback;
    private DateTime? _fallbackActivatedAt;

    /// <summary>How often to attempt restoring the primary provider when on fallback.</summary>
    private static readonly TimeSpan PrimaryRestorationInterval = TimeSpan.FromMinutes(5);

    /// <summary>Safety limit to prevent infinite tool loops.</summary>
    public int MaxToolIterations { get; set; } = 25;

    /// <summary>Maximum idle time between provider stream events before surfacing a timeout.</summary>
    public TimeSpan StreamWatchdogTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Max concurrent workers for parallel tool execution.</summary>
    private const int MaxParallelWorkers = 8;

    /// <summary>
    /// Read-only tools safe for concurrent execution.
    /// Names must match the runtime <see cref="ITool.Name"/> values registered with the agent —
    /// not human-readable variants. The web tools register as "webfetch"/"websearch"
    /// (no underscore), so any underscore variants would be silently bypassed by
    /// <see cref="ShouldParallelize"/> and never benefit from parallelization.
    /// </summary>
    private static readonly HashSet<string> ParallelSafeTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "glob", "grep", "webfetch", "websearch",
        "session_search", "skill_invoke", "memory", "lsp"
    };

    /// <summary>Tools that must never run in parallel.</summary>
    private static readonly HashSet<string> NeverParallelTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "ask_user"
    };

    /// <summary>Fired when an activity entry is added or updated.</summary>
    public event Action<ActivityEntry>? ActivityEntryAdded;

    /// <summary>Log of all tool activity entries for the current agent lifetime.</summary>
    public List<ActivityEntry> ActivityLog { get; } = new();

    /// <summary>Clear the activity log (e.g. on new chat).</summary>
    public void ClearActivityLog() => ActivityLog.Clear();

    /// <summary>
    /// Optional callback for interactive permission prompts.
    /// When PermissionBehavior.Ask is returned, this callback is invoked with
    /// (toolName, message, toolArguments). The third argument is the raw JSON
    /// arguments string the model passed to the tool — for the bash tool that
    /// is the actual shell command about to run, for read/write/edit it is the
    /// path and contents, etc. Surfacing it in the prompt UI lets technical
    /// users audit exactly what the agent is about to execute *before*
    /// approving it, instead of only seeing it after the fact in the activity
    /// log. May be null if the host did not capture the arguments.
    /// Returns true to allow, false to deny. If the callback itself is null,
    /// Ask defaults to deny.
    /// </summary>
    public Func<string, string, string?, Task<bool>>? PermissionPromptCallback { get; set; }

    public Agent(
        IChatClient chatClient,
        ILogger<Agent> logger,
        PermissionManager? permissions = null,
        TranscriptStore? transcripts = null,
        MemoryManager? memories = null,
        ContextManager? contextManager = null,
        SoulService? soulService = null,
        PluginManager? pluginManager = null,
        IChatClient? fallbackChatClient = null,
        CredentialPool? credentialPool = null)
    {
        _chatClient = chatClient;
        _logger = logger;
        _permissions = permissions;
        _transcripts = transcripts;
        _memories = memories;
        _contextManager = contextManager;
        _soulService = soulService;
        _pluginManager = pluginManager;
        _fallbackChatClient = fallbackChatClient;
        _credentialPool = credentialPool;
    }

    /// <summary>
    /// INV-004/005: Gets the active chat client, handling fallback and primary restoration.
    /// At the start of each turn, if on fallback and enough time has passed, try primary.
    /// </summary>
    private IChatClient GetActiveChatClient()
    {
        if (_usingFallback && _fallbackActivatedAt.HasValue)
        {
            var elapsed = DateTime.UtcNow - _fallbackActivatedAt.Value;
            if (elapsed >= PrimaryRestorationInterval)
            {
                // Check if credential pool has recovered
                if (_credentialPool is null || _credentialPool.HasHealthyCredentials)
                {
                    _logger.LogInformation("Attempting to restore primary provider after {Elapsed}s on fallback",
                        elapsed.TotalSeconds);
                    _usingFallback = false;
                    _fallbackActivatedAt = null;
                    return _chatClient;
                }
            }
            return _fallbackChatClient ?? _chatClient;
        }
        return _chatClient;
    }

    /// <summary>
    /// INV-004/005: Activates fallback provider after a primary provider failure.
    /// </summary>
    private IChatClient ActivateFallback(Exception ex)
    {
        if (_fallbackChatClient is not null && !_usingFallback)
        {
            _usingFallback = true;
            _fallbackActivatedAt = DateTime.UtcNow;
            _logger.LogWarning(ex, "Primary provider failed — activating fallback provider");
            return _fallbackChatClient;
        }
        throw ex; // No fallback available, rethrow
    }

    public void RegisterTool(ITool tool)
    {
        _tools[tool.Name] = tool;
        _logger.LogInformation("Registered tool: {ToolName}", tool.Name);
    }

    public IReadOnlyDictionary<string, ITool> Tools => _tools;

    /// <summary>Build ToolDefinition list from registered tools for the LLM.</summary>
    public List<ToolDefinition> GetToolDefinitions()
    {
        return _tools.Values.Select(t => new ToolDefinition
        {
            Name = t.Name,
            Description = t.Description,
            Parameters = BuildParameterSchema(t)
        }).ToList();
    }

    /// <summary>
    /// Full chat loop with tool calling. Sends the user message, then iterates:
    /// LLM responds -> if tool calls, execute them -> feed results back -> repeat
    /// until LLM produces a final text response or we hit MaxToolIterations.
    ///
    /// Lifecycle guarantees:
    ///   * Plugin/memory/soul system blocks injected for this turn are removed
    ///     from <c>session.Messages</c> in <c>finally</c> so they do not accumulate
    ///     across turns and silently exhaust the model's context window — this
    ///     was the root cause of the v2.4.0 "tool-limit exhaustion" regression.
    ///   * <see cref="PluginManager.OnTurnEndAsync"/> fires on every exit path
    ///     (no-tools fast path, normal completion, max-iteration fallback,
    ///     and exception unwind), so plugin state stays consistent even when
    ///     the model misbehaves.
    /// </summary>
    public async Task<string> ChatAsync(string message, Session session, CancellationToken ct)
    {
        // ── Plugin turn start ──
        if (_pluginManager is not null)
        {
            try { await _pluginManager.OnTurnStartAsync(session.Messages.Count, message, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Plugin OnTurnStart failed"); }
        }

        // Transient system messages injected at index 0 for THIS turn only.
        // Tracked so we can pop them in finally — see class doc above.
        int transientSystemMessages = 0;
        string finalResponse = "";

        try
        {
            // ── Plugin system prompt blocks (includes memory via BuiltinMemoryPlugin) ──
            if (_pluginManager is not null)
            {
                try
                {
                    var pluginBlocks = await _pluginManager.GetSystemPromptBlocksAsync(ct);
                    if (!string.IsNullOrWhiteSpace(pluginBlocks))
                    {
                        session.Messages.Insert(0, new Message
                        {
                            Role = "system",
                            Content = pluginBlocks
                        });
                        transientSystemMessages++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Plugin system prompt blocks failed");
                }
            }
            else
            {
                int before = session.Messages.Count;
                await AgentContextAssembler.InjectMemoriesAsync(
                    session, message, _tools.Keys, _memories, _logger, ct);
                transientSystemMessages += Math.Max(0, session.Messages.Count - before);
            }

            // ── Add user message ──
            await AgentSessionWriter.AppendUserMessageAsync(session, message, _transcripts, ct);

            _logger.LogInformation("Processing message for session {SessionId}", session.Id);

            int beforeSoul = session.Messages.Count;
            await AgentContextAssembler.InjectSoulFallbackAsync(session, _contextManager, _soulService, _logger);
            // Soul fallback inserts at index 0 too — track for cleanup.
            int afterSoul = session.Messages.Count;
            if (afterSoul > beforeSoul)
                transientSystemMessages += (afterSoul - beforeSoul);

            var preparedContext = await AgentContextAssembler.PrepareOptimizedContextAsync(
                session.Id, message, _contextManager, _logger, ct);

            if (_tools.Count == 0)
            {
                // No tools registered — simple completion
                var messagesToSend = preparedContext ?? session.Messages;
                // INV-004/005: Use active client (with fallback support)
                var activeClient = GetActiveChatClient();
                string response;
                try
                {
                    response = await activeClient.CompleteAsync(messagesToSend, ct);
                }
                catch (HttpRequestException ex) when (_fallbackChatClient is not null)
                {
                    activeClient = ActivateFallback(ex);
                    response = await activeClient.CompleteAsync(messagesToSend, ct);
                }
                await AgentSessionWriter.AppendAssistantMessageAsync(session, response, _transcripts, ct);
                if (_contextManager is not null)
                    await _contextManager.UpdateAfterResponseAsync(session.Id, ct: ct);

                finalResponse = response;
                return response;
            }

            var toolDefs = GetToolDefinitions();
            var iterations = 0;

            while (iterations < MaxToolIterations)
            {
            iterations++;

            // Use prepared context for first iteration, then fall back to session.Messages
            // because session.Messages accumulates tool results as the loop progresses
            var messagesToUse = (iterations == 1 && preparedContext is not null)
                ? preparedContext
                : session.Messages;

            // INV-004/005: Use active client with fallback support
            var activeClientForTools = GetActiveChatClient();
            ChatResponse response;
            try
            {
                response = await activeClientForTools.CompleteWithToolsAsync(messagesToUse, toolDefs, ct);
            }
            catch (HttpRequestException ex) when (_fallbackChatClient is not null)
            {
                activeClientForTools = ActivateFallback(ex);
                response = await activeClientForTools.CompleteWithToolsAsync(messagesToUse, toolDefs, ct);
            }

            if (!response.HasToolCalls)
            {
                // LLM is done — return final text
                var finalContent = response.Content ?? "";
                await AgentSessionWriter.AppendAssistantMessageAsync(session, finalContent, _transcripts, ct);
                if (_contextManager is not null)
                    await _contextManager.UpdateAfterResponseAsync(session.Id, ct: ct);
                finalResponse = finalContent;
                return finalContent;
            }

            // Normalize tool-call IDs for deterministic referencing across providers
            var normalizedToolCalls = NormalizeToolCallIds(response.ToolCalls!, iterations);

            // Record the assistant message with its tool call requests
            await AgentSessionWriter.AppendAssistantToolRequestMessageAsync(
                session, response.Content ?? "", normalizedToolCalls, _transcripts, ct);

            // Execute tool calls — parallel when safe, sequential otherwise
            var toolCallsList = normalizedToolCalls;

            if (ShouldParallelize(toolCallsList))
            {
                // ── Parallel execution path (read-only tools only) ──
                _logger.LogInformation("Executing {Count} tool calls in parallel", toolCallsList.Count);
                var parallelResults = await ExecuteToolCallsParallelAsync(toolCallsList, ct);

                foreach (var (toolCall, result, durationMs) in parallelResults)
                {
                    var entry = new ActivityEntry
                    {
                        ToolName = toolCall.Name,
                        ToolCallId = toolCall.Id,
                        InputSummary = Truncate(toolCall.Arguments, 200),
                        Status = result.Success ? ActivityStatus.Success : ActivityStatus.Failed,
                        OutputSummary = Truncate(result.Content, 200),
                        DurationMs = durationMs
                    };
                    ActivityLog.Add(entry);
                    ActivityEntryAdded?.Invoke(entry);
                    if (_transcripts is not null)
                        await _transcripts.SaveActivityAsync(session.Id, entry, ct);

                    var resultContent = result.Content;
                    if (SecretScanner.ContainsSecrets(resultContent))
                        resultContent = SecretScanner.RedactSecrets(resultContent);

                    var toolResultMsg = new Message
                    {
                        Role = "tool",
                        Content = resultContent,
                        ToolCallId = toolCall.Id,
                        ToolName = toolCall.Name
                    };
                    session.AddMessage(toolResultMsg);
                    if (_transcripts is not null)
                        await _transcripts.SaveMessageAsync(session.Id, toolResultMsg, ct);
                }
            }
            else
            {
            // ── Sequential execution path (default, with permissions) ──
            foreach (var toolCall in toolCallsList)
            {
                // ── Permission gate ──
                if (_permissions is not null)
                {
                    try
                    {
                        var decision = await _permissions.CheckPermissionsAsync(
                            toolCall.Name, toolCall.Arguments, ct);

                        if (decision.Behavior == PermissionBehavior.Deny)
                        {
                            // Track denial in activity log
                            var deniedEntry = new ActivityEntry
                            {
                                ToolName = toolCall.Name,
                                ToolCallId = toolCall.Id,
                                InputSummary = Truncate(toolCall.Arguments, 200),
                                OutputSummary = $"Permission denied: {decision.DecisionReason ?? decision.Message ?? "Blocked"}",
                                Status = ActivityStatus.Denied
                            };
                            ActivityLog.Add(deniedEntry);
                            ActivityEntryAdded?.Invoke(deniedEntry);
                            if (_transcripts is not null)
                                await _transcripts.SaveActivityAsync(session.Id, deniedEntry, ct);

                            var denialMsg = new Message
                            {
                                Role = "tool",
                                Content = $"Permission denied: {decision.DecisionReason ?? decision.Message ?? "Blocked by permission rule"}",
                                ToolCallId = toolCall.Id,
                                ToolName = toolCall.Name
                            };
                            session.AddMessage(denialMsg);
                            if (_transcripts is not null)
                                await _transcripts.SaveMessageAsync(session.Id, denialMsg, ct);
                            continue;
                        }

                        if (decision.Behavior == PermissionBehavior.Ask)
                        {
                            var permissionMessage = decision.Message ?? "This operation requires permission";
                            bool allowed = false;

                            if (PermissionPromptCallback is not null)
                            {
                                try
                                {
                                    // Pass the raw tool arguments (the actual command/path/payload
                                    // the model wants the tool to run) to the prompt UI so the user
                                    // can audit it before approving — see PermissionPromptCallback
                                    // doc above.
                                    allowed = await PermissionPromptCallback(toolCall.Name, permissionMessage, toolCall.Arguments);
                                }
                                catch (Exception promptEx)
                                {
                                    _logger.LogWarning(promptEx, "Permission prompt callback failed, denying");
                                }
                            }

                            if (!allowed)
                            {
                                // Track user-denied in activity log
                                var userDeniedEntry = new ActivityEntry
                                {
                                    ToolName = toolCall.Name,
                                    ToolCallId = toolCall.Id,
                                    InputSummary = Truncate(toolCall.Arguments, 200),
                                    OutputSummary = $"Permission denied by user: {permissionMessage}",
                                    Status = ActivityStatus.Denied
                                };
                                ActivityLog.Add(userDeniedEntry);
                                ActivityEntryAdded?.Invoke(userDeniedEntry);
                                if (_transcripts is not null)
                                    await _transcripts.SaveActivityAsync(session.Id, userDeniedEntry, ct);

                                var askMsg = new Message
                                {
                                    Role = "tool",
                                    Content = $"Permission denied by user: {permissionMessage}",
                                    ToolCallId = toolCall.Id,
                                    ToolName = toolCall.Name
                                };
                                session.AddMessage(askMsg);
                                if (_transcripts is not null)
                                    await _transcripts.SaveMessageAsync(session.Id, askMsg, ct);
                                continue;
                            }
                            // User approved — fall through to execute the tool
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Permission check failed for {ToolName}, allowing execution", toolCall.Name);
                    }
                }

                // ── Activity tracking: BEFORE execution ──
                var activityEntry = new ActivityEntry
                {
                    ToolName = toolCall.Name,
                    ToolCallId = toolCall.Id,
                    InputSummary = Truncate(toolCall.Arguments, 200),
                    Status = ActivityStatus.Running
                };
                ActivityLog.Add(activityEntry);
                ActivityEntryAdded?.Invoke(activityEntry);

                _logger.LogInformation("Executing tool {ToolName} (call {CallId})", toolCall.Name, toolCall.Id);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = await ExecuteToolCallAsync(toolCall, ct);
                sw.Stop();

                // ── Activity tracking: AFTER execution ──
                activityEntry.DurationMs = sw.ElapsedMilliseconds;
                activityEntry.Status = result.Success ? ActivityStatus.Success : ActivityStatus.Failed;
                activityEntry.OutputSummary = Truncate(result.Content, 200);

                // ── Soul: record mistakes on tool failure ──
                if (!result.Success && _soulService is not null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _soulService.RecordMistakeAsync(new MistakeEntry
                            {
                                Context = $"Tool: {toolCall.Name}, Args: {Truncate(toolCall.Arguments, 100)}",
                                Mistake = $"Tool execution failed: {Truncate(result.Content, 200)}",
                                Correction = "Tool returned an error — review approach",
                                Lesson = $"When using {toolCall.Name}, verify inputs before execution"
                            });
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "RecordMistakeAsync failed in background task");
                        }
                    }, CancellationToken.None);
                }

                // Detect diff content
                if (result.Content.Contains("--- a/") || result.Content.Contains("+++ b/"))
                {
                    activityEntry.DiffPreview = Truncate(result.Content, 2000);
                }

                ActivityEntryAdded?.Invoke(activityEntry);
                if (_transcripts is not null)
                    await _transcripts.SaveActivityAsync(session.Id, activityEntry, ct);

                // ── Secret exfiltration scan ──
                var resultContent = result.Content;
                if (SecretScanner.ContainsSecrets(resultContent))
                {
                    _logger.LogWarning("Secrets detected in tool result from {ToolName}, redacting", toolCall.Name);
                    resultContent = SecretScanner.RedactSecrets(resultContent);
                }

                var toolResultMsg = new Message
                {
                    Role = "tool",
                    Content = resultContent,
                    ToolCallId = toolCall.Id,
                    ToolName = toolCall.Name
                };
                session.AddMessage(toolResultMsg);
                if (_transcripts is not null)
                    await _transcripts.SaveMessageAsync(session.Id, toolResultMsg, ct);
            }
            } // end else (sequential path)
        }

            _logger.LogWarning("Hit max tool iterations ({Max}) for session {SessionId}", MaxToolIterations, session.Id);
            var fallback = "I've reached the maximum number of tool call iterations. Here's what I've accomplished so far based on the conversation above.";
            await AgentSessionWriter.AppendAssistantMessageAsync(session, fallback, _transcripts, ct);
            if (_contextManager is not null)
                await _contextManager.UpdateAfterResponseAsync(session.Id, ct: ct);
            finalResponse = fallback;
            return fallback;
        }
        finally
        {
            // ── Pop transient system messages so they don't accumulate across turns ──
            // Plugin/memory/soul blocks are regenerated every turn against the latest
            // session state; if we leave them in session.Messages, we both bloat the
            // context window AND duplicate stale snapshots that confuse the model.
            // This is the regression that surfaced as "tool-limit exhaustion" in v2.4.0.
            for (int i = 0; i < transientSystemMessages && session.Messages.Count > 0; i++)
            {
                if (session.Messages[0].Role == "system")
                    session.Messages.RemoveAt(0);
                else
                    break; // Defensive: shape changed underneath us; stop rather than corrupt.
            }

            // ── Plugin turn end fires on EVERY exit path (success, no-tools, max-iter, exception, cancellation) ──
            // Routed through FirePluginTurnEndAsync so cleanup runs on a fresh
            // bounded-timeout token instead of the original (possibly-cancelled)
            // turn token. Without this, user-cancellation mid-turn would skip
            // any plugin work that respects the token — silently violating the
            // "always-fires" guarantee promised in the class doc.
            await FirePluginTurnEndAsync(message, finalResponse, session.Id, "chat");
        }
    }

    /// <summary>
    /// Streaming chat loop with tool calling. Mirrors ChatAsync but yields StreamEvent
    /// objects as they arrive. Tool-calling turns use non-streaming CompleteWithToolsAsync
    /// (same as the Python agent), but the final text response is streamed token-by-token.
    /// </summary>
    public async IAsyncEnumerable<StreamEvent> StreamChatAsync(
        string message, Session session,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Track transient system messages injected this turn (memory + soul) so we
        // can pop them in finally and avoid the v2.4.0 context-bloat regression
        // where every streaming turn left another stale snapshot at index 0.
        int transientSystemMessages = 0;

        // Streamed text accumulator. Fed into OnTurnEndAsync so plugins observing
        // assistant output (e.g. learning plugins, transcript indexers) see the
        // SAME final text the user saw — even when streaming is interrupted by
        // cancellation, errors, or the max-iteration fallback. Built up by
        // intercepting every TokenDelta yielded downstream.
        var streamedResponse = new System.Text.StringBuilder();

        // ── Plugin turn start ──
        // StreamChatAsync historically skipped the plugin lifecycle entirely,
        // so plugins observed only non-streaming turns and silently desynced
        // when the UI used streaming. Mirror ChatAsync semantics: fire start
        // here, and rely on the bottom finally to fire end on every exit path.
        if (_pluginManager is not null)
        {
            try { await _pluginManager.OnTurnStartAsync(session.Messages.Count, message, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Plugin OnTurnStart failed (stream)"); }
        }

        // ── Plugin system prompt blocks ──
        // Same source of truth as ChatAsync. Without these, the streaming path
        // ignores the BuiltinMemoryPlugin and any registered context plugins,
        // producing visibly different model behavior between streaming and
        // non-streaming flows.
        if (_pluginManager is not null)
        {
            try
            {
                var pluginBlocks = await _pluginManager.GetSystemPromptBlocksAsync(ct);
                if (!string.IsNullOrWhiteSpace(pluginBlocks))
                {
                    session.Messages.Insert(0, new Message
                    {
                        Role = "system",
                        Content = pluginBlocks
                    });
                    transientSystemMessages++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Plugin system prompt blocks failed (stream)");
            }
        }

        // ── Memory injection ──
        // Only run the legacy direct-memory path when the plugin manager is
        // ABSENT — otherwise BuiltinMemoryPlugin already injected the same
        // content via GetSystemPromptBlocksAsync above and we'd double-stamp.
        if (_pluginManager is null && _memories is not null)
        {
            try
            {
                var recentTools = _tools.Keys.Take(10).ToList();
                var relevantMemories = await _memories.LoadRelevantMemoriesAsync(message, recentTools, ct);
                if (relevantMemories.Count > 0)
                {
                    var memoryBlock = string.Join("\n---\n",
                        relevantMemories.Select(m => $"[{m.Type}] {m.Filename}:\n{m.Content}"));
                    session.Messages.Insert(0, new Message
                    {
                        Role = "system",
                        Content = $"[Relevant Memories]\n{memoryBlock}"
                    });
                    transientSystemMessages++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load memories, continuing without them");
            }
        }

        // ── Add user message ──
        var userMessage = new Message { Role = "user", Content = message };
        session.AddMessage(userMessage);
        if (_transcripts is not null)
            await _transcripts.SaveMessageAsync(session.Id, userMessage, ct);

        _logger.LogInformation("Processing streaming message for session {SessionId}", session.Id);

        // ── Soul injection (fallback path — when ContextManager is null) ──
        if (_contextManager is null && _soulService is not null)
        {
            try
            {
                var soulContext = await _soulService.AssembleSoulContextAsync();
                if (!string.IsNullOrWhiteSpace(soulContext))
                {
                    session.Messages.Insert(0, new Message
                    {
                        Role = "system",
                        Content = soulContext
                    });
                    transientSystemMessages++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load soul context, continuing without it");
            }
        }

        // Wrap the rest of the iterator in try/finally so transient system messages
        // get cleaned up on every exit path — including yield break, exception unwind,
        // and consumer-driven cancellation. C# iterators allow `yield return` inside
        // a try block that has a finally clause.
        try
        {
        // ── Context manager integration ──
        List<Message>? preparedContext = null;
        if (_contextManager is not null)
        {
            try
            {
                preparedContext = await _contextManager.PrepareContextAsync(
                    session.Id, message, retrievedContext: null, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ContextManager failed, falling back to raw session messages");
            }
        }

        if (_tools.Count == 0)
        {
            // No tools registered — stream the response directly
            var messagesToSend = preparedContext ?? session.Messages;

            await foreach (var evt in WatchProviderStreamAsync(
                _chatClient.StreamAsync(null, messagesToSend, null, ct), ct))
            {
                if (evt is StreamEvent.TokenDelta td)
                    streamedResponse.Append(td.Text);

                yield return evt;
            }

            // Save accumulated response — always save, even if empty, to match ChatAsync behavior
            var assistantMsg = new Message { Role = "assistant", Content = streamedResponse.ToString() };
            session.AddMessage(assistantMsg);
            if (_transcripts is not null)
                await _transcripts.SaveMessageAsync(session.Id, assistantMsg, ct);
            if (_contextManager is not null)
                await _contextManager.UpdateAfterResponseAsync(session.Id, ct: ct);
            yield break;
        }

        // ── Tool-calling loop ──
        var toolDefs = GetToolDefinitions();
        var iterations = 0;

        while (iterations < MaxToolIterations)
        {
            iterations++;

            var messagesToUse = (iterations == 1 && preparedContext is not null)
                ? preparedContext
                : session.Messages;

            var response = await _chatClient.CompleteWithToolsAsync(messagesToUse, toolDefs, ct);

            if (!response.HasToolCalls)
            {
                // LLM is done — stream the final text as a single token delta
                var finalContent = response.Content ?? "";
                if (!string.IsNullOrEmpty(finalContent))
                {
                    streamedResponse.Append(finalContent);
                    yield return new StreamEvent.TokenDelta(finalContent);
                }

                var finalMsg = new Message { Role = "assistant", Content = finalContent };
                session.AddMessage(finalMsg);
                if (_transcripts is not null)
                    await _transcripts.SaveMessageAsync(session.Id, finalMsg, ct);
                if (_contextManager is not null)
                    await _contextManager.UpdateAfterResponseAsync(session.Id, ct: ct);
                yield break;
            }

            // Normalize tool-call IDs for deterministic referencing across providers
            var normalizedStreamToolCalls = NormalizeToolCallIds(response.ToolCalls!, iterations);

            // Record assistant message with tool call requests
            var assistantToolMsg = new Message
            {
                Role = "assistant",
                Content = response.Content ?? "",
                ToolCalls = normalizedStreamToolCalls
            };
            session.AddMessage(assistantToolMsg);
            if (_transcripts is not null)
                await _transcripts.SaveMessageAsync(session.Id, assistantToolMsg, ct);

            // Execute each tool call
            foreach (var toolCall in normalizedStreamToolCalls)
            {
                // ── Permission gate ──
                if (_permissions is not null)
                {
                    try
                    {
                        var decision = await _permissions.CheckPermissionsAsync(
                            toolCall.Name, toolCall.Arguments, ct);

                        if (decision.Behavior == PermissionBehavior.Deny)
                        {
                            var deniedEntry = new ActivityEntry
                            {
                                ToolName = toolCall.Name,
                                ToolCallId = toolCall.Id,
                                InputSummary = Truncate(toolCall.Arguments, 200),
                                OutputSummary = $"Permission denied: {decision.DecisionReason ?? decision.Message ?? "Blocked"}",
                                Status = ActivityStatus.Denied
                            };
                            ActivityLog.Add(deniedEntry);
                            ActivityEntryAdded?.Invoke(deniedEntry);
                            if (_transcripts is not null)
                                await _transcripts.SaveActivityAsync(session.Id, deniedEntry, ct);

                            var denialMsg = new Message
                            {
                                Role = "tool",
                                Content = $"Permission denied: {decision.DecisionReason ?? decision.Message ?? "Blocked by permission rule"}",
                                ToolCallId = toolCall.Id,
                                ToolName = toolCall.Name
                            };
                            session.AddMessage(denialMsg);
                            if (_transcripts is not null)
                                await _transcripts.SaveMessageAsync(session.Id, denialMsg, ct);
                            continue;
                        }

                        if (decision.Behavior == PermissionBehavior.Ask)
                        {
                            var permissionMessage = decision.Message ?? "This operation requires permission";
                            bool allowed = false;

                            if (PermissionPromptCallback is not null)
                            {
                                try
                                {
                                    allowed = await PermissionPromptCallback(toolCall.Name, permissionMessage, toolCall.Arguments);
                                }
                                catch (Exception promptEx)
                                {
                                    _logger.LogWarning(promptEx, "Permission prompt callback failed, denying");
                                }
                            }

                            if (!allowed)
                            {
                                var userDeniedEntry = new ActivityEntry
                                {
                                    ToolName = toolCall.Name,
                                    ToolCallId = toolCall.Id,
                                    InputSummary = Truncate(toolCall.Arguments, 200),
                                    OutputSummary = $"Permission denied by user: {permissionMessage}",
                                    Status = ActivityStatus.Denied
                                };
                                ActivityLog.Add(userDeniedEntry);
                                ActivityEntryAdded?.Invoke(userDeniedEntry);
                                if (_transcripts is not null)
                                    await _transcripts.SaveActivityAsync(session.Id, userDeniedEntry, ct);

                                var askMsg = new Message
                                {
                                    Role = "tool",
                                    Content = $"Permission denied by user: {permissionMessage}",
                                    ToolCallId = toolCall.Id,
                                    ToolName = toolCall.Name
                                };
                                session.AddMessage(askMsg);
                                if (_transcripts is not null)
                                    await _transcripts.SaveMessageAsync(session.Id, askMsg, ct);
                                continue;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Permission check failed for {ToolName}, allowing execution", toolCall.Name);
                    }
                }

                // Yield tool-calling status to the UI
                yield return new StreamEvent.TokenDelta($"\n[Calling tool: {toolCall.Name}]\n");

                // ── Activity tracking: BEFORE execution ──
                var activityEntry = new ActivityEntry
                {
                    ToolName = toolCall.Name,
                    ToolCallId = toolCall.Id,
                    InputSummary = Truncate(toolCall.Arguments, 200),
                    Status = ActivityStatus.Running
                };
                ActivityLog.Add(activityEntry);
                ActivityEntryAdded?.Invoke(activityEntry);

                _logger.LogInformation("Executing tool {ToolName} (call {CallId})", toolCall.Name, toolCall.Id);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = await ExecuteToolCallAsync(toolCall, ct);
                sw.Stop();

                // ── Activity tracking: AFTER execution ──
                activityEntry.DurationMs = sw.ElapsedMilliseconds;
                activityEntry.Status = result.Success ? ActivityStatus.Success : ActivityStatus.Failed;
                activityEntry.OutputSummary = Truncate(result.Content, 200);

                // ── Soul: record mistakes on tool failure ──
                if (!result.Success && _soulService is not null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _soulService.RecordMistakeAsync(new MistakeEntry
                            {
                                Context = $"Tool: {toolCall.Name}, Args: {Truncate(toolCall.Arguments, 100)}",
                                Mistake = $"Tool execution failed: {Truncate(result.Content, 200)}",
                                Correction = "Tool returned an error — review approach",
                                Lesson = $"When using {toolCall.Name}, verify inputs before execution"
                            });
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "RecordMistakeAsync failed in background task");
                        }
                    }, CancellationToken.None);
                }

                if (result.Content.Contains("--- a/") || result.Content.Contains("+++ b/"))
                {
                    activityEntry.DiffPreview = Truncate(result.Content, 2000);
                }

                ActivityEntryAdded?.Invoke(activityEntry);
                if (_transcripts is not null)
                    await _transcripts.SaveActivityAsync(session.Id, activityEntry, ct);

                // ── Secret exfiltration scan ──
                var resultContent = result.Content;
                if (SecretScanner.ContainsSecrets(resultContent))
                {
                    _logger.LogWarning("Secrets detected in tool result from {ToolName}, redacting", toolCall.Name);
                    resultContent = SecretScanner.RedactSecrets(resultContent);
                }

                var toolResultMsg = new Message
                {
                    Role = "tool",
                    Content = resultContent,
                    ToolCallId = toolCall.Id,
                    ToolName = toolCall.Name
                };
                session.AddMessage(toolResultMsg);
                if (_transcripts is not null)
                    await _transcripts.SaveMessageAsync(session.Id, toolResultMsg, ct);
            }
        }

        // Hit max iterations
        _logger.LogWarning("Hit max tool iterations ({Max}) for session {SessionId}", MaxToolIterations, session.Id);
        var fallback = "I've reached the maximum number of tool call iterations. Here's what I've accomplished so far based on the conversation above.";
        streamedResponse.Append(fallback);
        yield return new StreamEvent.TokenDelta(fallback);
        var fallbackMsg = new Message { Role = "assistant", Content = fallback };
        session.AddMessage(fallbackMsg);
        if (_transcripts is not null)
            await _transcripts.SaveMessageAsync(session.Id, fallbackMsg, ct);
        if (_contextManager is not null)
            await _contextManager.UpdateAfterResponseAsync(session.Id, ct: ct);
        }
        finally
        {
            // Pop transient system messages so they do not leak across turns.
            // See ChatAsync for the full rationale.
            for (int i = 0; i < transientSystemMessages && session.Messages.Count > 0; i++)
            {
                if (session.Messages[0].Role == "system")
                    session.Messages.RemoveAt(0);
                else
                    break;
            }

            // ── Plugin turn end fires on EVERY exit path ──
            // streamedResponse holds whatever text actually reached the consumer
            // before exit. On consumer-cancellation or mid-stream error it will
            // be a partial response; that's the right value for plugins observing
            // user-visible output (don't pretend the assistant said more than it
            // did). On normal completion it equals the saved assistant message.
            //
            // Routed through FirePluginTurnEndAsync so the call survives an
            // already-cancelled `ct` (very common on the streaming path because
            // the UI cancels the moment the user clicks Stop).
            await FirePluginTurnEndAsync(message, streamedResponse.ToString(), session.Id, "stream");
        }
    }

    private async IAsyncEnumerable<StreamEvent> WatchProviderStreamAsync(
        IAsyncEnumerable<StreamEvent> stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var enumerator = stream.GetAsyncEnumerator(watchdogCts.Token);

        try
        {
            while (true)
            {
                var moveNextTask = enumerator.MoveNextAsync().AsTask();
                var timeoutTask = Task.Delay(StreamWatchdogTimeout, ct);
                var completed = await Task.WhenAny(moveNextTask, timeoutTask);

                if (completed == timeoutTask)
                {
                    if (ct.IsCancellationRequested)
                        throw new OperationCanceledException(ct);

                    await watchdogCts.CancelAsync();
                    yield return new StreamEvent.StreamError(
                        LlmProviderException.Timeout(new TimeoutException(
                            $"Provider stream produced no events for {StreamWatchdogTimeout.TotalSeconds:0.#} seconds.")),
                        ProviderErrorCode.ProviderTimeout);
                    yield break;
                }

                bool hasNext = false;
                StreamEvent.StreamError? streamError = null;
                try
                {
                    hasNext = await moveNextTask;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException ex)
                {
                    streamError = new StreamEvent.StreamError(
                        LlmProviderException.Timeout(ex),
                        ProviderErrorCode.ProviderTimeout);
                }
                catch (HttpRequestException ex)
                {
                    var providerError = LlmProviderException.FromHttp(ex);
                    streamError = new StreamEvent.StreamError(providerError, providerError.Code);
                }
                catch (JsonException ex)
                {
                    var providerError = LlmProviderException.StreamParse(ex);
                    streamError = new StreamEvent.StreamError(providerError, providerError.Code);
                }
                catch (Exception ex)
                {
                    var providerError = LlmProviderException.Transport(ex);
                    streamError = new StreamEvent.StreamError(providerError, providerError.Code);
                }

                if (streamError is not null)
                {
                    yield return streamError;
                    yield break;
                }

                if (!hasNext)
                    yield break;

                yield return enumerator.Current;
            }
        }
        finally
        {
            try
            {
                await enumerator.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Provider stream enumerator disposal failed");
            }
        }
    }

    /// <summary>
    /// Maximum time we'll wait for a plugin's <c>OnTurnEnd</c> cleanup before
    /// abandoning it. Picked to be long enough for legitimate persistence work
    /// (transcript flush, learning-plugin index update, telemetry batch) but
    /// short enough that an app shutdown isn't held hostage by a misbehaving
    /// plugin. Tune via PR if real-world plugins need more headroom — but raise
    /// it deliberately, not by accident.
    /// </summary>
    private static readonly TimeSpan PluginCleanupTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Fire <see cref="PluginManager.OnTurnEndAsync"/> on a DETACHED token with
    /// a bounded timeout so cleanup runs even when the original turn token
    /// is already cancelled — preserving the "always-fires" guarantee we
    /// established when fixing the v2.4.0 lifecycle regression.
    ///
    /// Why detach: the original <c>ct</c> may already be cancelled by the time
    /// we hit the <c>finally</c> (user clicked Stop, request aborted, etc.).
    /// Passing that token straight to the plugin causes any well-behaved plugin
    /// — i.e. one that calls <c>ct.ThrowIfCancellationRequested()</c> or forwards
    /// the token to async I/O — to abort BEFORE persisting state. That silently
    /// breaks the lifecycle invariant the surrounding comments promise.
    ///
    /// Why bound: a plugin that ignores cancellation and hangs (e.g. a network
    /// post with no client-side timeout) would otherwise stall the entire turn
    /// teardown and, transitively, app shutdown. The bounded timeout caps that
    /// blast radius.
    ///
    /// Exceptions are swallowed (logged) so plugin bugs never bubble up after
    /// the model has already returned its final answer to the caller.
    /// </summary>
    private async Task FirePluginTurnEndAsync(
        string message, string finalResponse, string sessionId, string contextLabel)
    {
        if (_pluginManager is null) return;

        using var cleanupCts = new CancellationTokenSource(PluginCleanupTimeout);
        try
        {
            await _pluginManager.OnTurnEndAsync(message, finalResponse, sessionId, cleanupCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Plugin OnTurnEnd failed ({Context})", contextLabel);
        }
    }

    /// <summary>Determine if a batch of tool calls can be safely parallelized.</summary>
    private static bool ShouldParallelize(IReadOnlyList<ToolCall> toolCalls)
    {
        if (toolCalls.Count <= 1) return false;
        if (toolCalls.Any(tc => NeverParallelTools.Contains(tc.Name))) return false;
        return toolCalls.All(tc => ParallelSafeTools.Contains(tc.Name));
    }

    /// <summary>Execute a batch of tool calls in parallel using a semaphore to limit concurrency.</summary>
    private async Task<List<(ToolCall Call, ToolResult Result, long DurationMs)>> ExecuteToolCallsParallelAsync(
        IReadOnlyList<ToolCall> toolCalls, CancellationToken ct)
    {
        using var semaphore = new SemaphoreSlim(MaxParallelWorkers);
        var tasks = toolCalls.Select(async toolCall =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = await ExecuteToolCallAsync(toolCall, ct);
                sw.Stop();
                return (Call: toolCall, Result: result, DurationMs: sw.ElapsedMilliseconds);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    private async Task<ToolResult> ExecuteToolCallAsync(ToolCall toolCall, CancellationToken ct)
    {
        if (!_tools.TryGetValue(toolCall.Name, out var tool))
        {
            _logger.LogWarning("Unknown tool requested: {ToolName}", toolCall.Name);
            return ToolResult.Fail($"Unknown tool: {toolCall.Name}");
        }

        try
        {
            var parameters = JsonSerializer.Deserialize(toolCall.Arguments, tool.ParametersType, ToolArgJsonOptions)
                ?? throw new JsonException($"Failed to deserialize arguments for {toolCall.Name}");
            return await tool.ExecuteAsync(parameters, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool {ToolName} failed", toolCall.Name);
            return ToolResult.Fail($"Tool execution failed: {ex.Message}", ex);
        }
    }

    private static JsonElement BuildParameterSchema(ITool tool)
    {
        if (tool is IToolSchemaProvider schemaProvider)
        {
            var customSchema = schemaProvider.GetParameterSchema();
            if (customSchema.HasValue && customSchema.Value.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
                return customSchema.Value.Clone();
        }

        // Build a JSON Schema from the tool's ParametersType using reflection
        var props = tool.ParametersType.GetProperties();
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var prop in props)
        {
            var propType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            var jsonType = propType switch
            {
                Type t when t == typeof(string) => "string",
                Type t when t == typeof(int) || t == typeof(long) => "integer",
                Type t when t == typeof(double) || t == typeof(float) => "number",
                Type t when t == typeof(bool) => "boolean",
                Type t when t.IsArray || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) => "array",
                _ => "string"
            };

            var propSchema = new Dictionary<string, object> { ["type"] = jsonType };

            // Check for Description attribute or XML doc
            var descAttr = prop.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
                .FirstOrDefault() as System.ComponentModel.DescriptionAttribute;
            if (descAttr is not null)
                propSchema["description"] = descAttr.Description;

            properties[ToCamelCase(prop.Name)] = propSchema;

            // Non-nullable value types are required; nullable properties are not
            if (propType.IsValueType && Nullable.GetUnderlyingType(prop.PropertyType) is null)
                required.Add(ToCamelCase(prop.Name));
        }

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties
        };
        if (required.Count > 0)
            schema["required"] = required;

        var json = JsonSerializer.Serialize(schema);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static readonly JsonSerializerOptions ToolArgJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static string ToCamelCase(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];

    private static string Truncate(string value, int maxLength) =>
        string.IsNullOrEmpty(value) ? "" :
        value.Length <= maxLength ? value :
        value[..maxLength] + "...";

    /// <summary>
    /// Deterministic tool-call ID normalization. Ensures stable IDs when provider-generated
    /// IDs are missing or empty, preventing prompt-cache invalidation across providers.
    /// </summary>
    private static string NormalizeToolCallId(string id, int turnNumber, int callIndex)
    {
        if (!string.IsNullOrEmpty(id)) return id;
        // Generate deterministic ID as fallback
        return $"call_{turnNumber}_{callIndex}";
    }

    /// <summary>
    /// Normalizes all tool-call IDs in a response, ensuring deterministic fallbacks
    /// for any missing IDs before they are stored in the session.
    /// </summary>
    private static List<ToolCall> NormalizeToolCallIds(IReadOnlyList<ToolCall> toolCalls, int turnNumber)
    {
        var result = new List<ToolCall>(toolCalls.Count);
        for (int i = 0; i < toolCalls.Count; i++)
        {
            var tc = toolCalls[i];
            var normalizedId = NormalizeToolCallId(tc.Id, turnNumber, i);
            if (normalizedId == tc.Id)
            {
                result.Add(tc);
            }
            else
            {
                result.Add(new ToolCall
                {
                    Id = normalizedId,
                    Name = tc.Name,
                    Arguments = tc.Arguments
                });
            }
        }
        return result;
    }
}
