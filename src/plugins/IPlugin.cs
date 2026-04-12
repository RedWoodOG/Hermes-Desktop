namespace Hermes.Agent.Plugins;

using Hermes.Agent.Core;
using Hermes.Agent.LLM;

// ══════════════════════════════════════════════
// Plugin Interface — lifecycle contract matching upstream
// ══════════════════════════════════════════════
//
// Upstream ref: agent/memory_manager.py MemoryProvider interface
// Adapted for C# with async Task, tool schema via ITool, and
// failure isolation per-plugin in PluginManager dispatch.

/// <summary>
/// Plugin interface for extending Hermes with external providers.
/// Each hook is optional — return default/empty if not applicable.
/// Exceptions are caught per-plugin and never block other plugins or the agent.
/// </summary>
public interface IPlugin
{
    /// <summary>Plugin identifier (e.g., "builtin-memory", "supermemory").</summary>
    string Name { get; }

    /// <summary>Whether this is a built-in plugin (always accepted) vs external (max 1 per category).</summary>
    bool IsBuiltin { get; }

    /// <summary>Plugin category for single-external-provider constraint (e.g., "memory").</summary>
    string Category { get; }

    // ── Initialization ──

    /// <summary>Called once after registration. Receives hermesHome for path resolution.</summary>
    Task InitializeAsync(PluginContext context, CancellationToken ct);

    /// <summary>Graceful shutdown — flush state, close connections.</summary>
    Task ShutdownAsync();

    // ── System Prompt ──

    /// <summary>Contribute a block to the system prompt (e.g., memory context, identity).</summary>
    Task<string?> GetSystemPromptBlockAsync(CancellationToken ct);

    // ── Turn Lifecycle ──

    /// <summary>Called at the start of each turn. Prefetch context, warm caches.</summary>
    Task OnTurnStartAsync(int turnNumber, string userMessage, CancellationToken ct);

    /// <summary>Called after a turn completes. Persist state, sync memories.</summary>
    Task OnTurnEndAsync(string userMessage, string assistantResponse, string sessionId, CancellationToken ct);

    /// <summary>Called when a session ends. Flush memories, save state.</summary>
    Task OnSessionEndAsync(IReadOnlyList<Message> messages, CancellationToken ct);

    /// <summary>Called before context compression. Last chance to save important data.</summary>
    Task OnPreCompressAsync(IReadOnlyList<Message> messages, CancellationToken ct);

    // ── Tool Integration ──

    /// <summary>Return tool schemas this plugin exposes (e.g., memory_write, memory_search).</summary>
    IReadOnlyList<ITool> GetTools();

    // ── Memory Events ──

    /// <summary>Called when the built-in memory system writes. External providers can mirror.</summary>
    Task OnMemoryWriteAsync(string action, string target, string content, CancellationToken ct);

    /// <summary>Called when a subagent delegation completes.</summary>
    Task OnDelegationCompleteAsync(string task, string result, string? childSessionId, CancellationToken ct);
}

/// <summary>
/// Context passed to plugins during initialization.
/// </summary>
public sealed class PluginContext
{
    public required string HermesHome { get; init; }
    public required string SessionId { get; init; }
    public required IChatClient ChatClient { get; init; }
    public IReadOnlyDictionary<string, string> Config { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Base class providing no-op defaults for all hooks.
/// Plugins override only what they need.
/// </summary>
public abstract class PluginBase : IPlugin
{
    public abstract string Name { get; }
    public virtual bool IsBuiltin => false;
    public virtual string Category => "general";

    public virtual Task InitializeAsync(PluginContext context, CancellationToken ct) => Task.CompletedTask;
    public virtual Task ShutdownAsync() => Task.CompletedTask;
    public virtual Task<string?> GetSystemPromptBlockAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    public virtual Task OnTurnStartAsync(int turnNumber, string userMessage, CancellationToken ct) => Task.CompletedTask;
    public virtual Task OnTurnEndAsync(string userMessage, string assistantResponse, string sessionId, CancellationToken ct) => Task.CompletedTask;
    public virtual Task OnSessionEndAsync(IReadOnlyList<Message> messages, CancellationToken ct) => Task.CompletedTask;
    public virtual Task OnPreCompressAsync(IReadOnlyList<Message> messages, CancellationToken ct) => Task.CompletedTask;
    public virtual IReadOnlyList<ITool> GetTools() => Array.Empty<ITool>();
    public virtual Task OnMemoryWriteAsync(string action, string target, string content, CancellationToken ct) => Task.CompletedTask;
    public virtual Task OnDelegationCompleteAsync(string task, string result, string? childSessionId, CancellationToken ct) => Task.CompletedTask;
}
