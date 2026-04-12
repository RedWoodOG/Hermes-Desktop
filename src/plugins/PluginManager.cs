namespace Hermes.Agent.Plugins;

using Hermes.Agent.Core;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text;

// ══════════════════════════════════════════════
// Plugin Manager — registration, dispatch, isolation
// ══════════════════════════════════════════════
//
// Upstream ref: agent/memory_manager.py MemoryManager
// Key patterns preserved:
//   - Single external provider per category
//   - Failure isolation (exceptions per-plugin never block others)
//   - Tool routing via _toolToPlugin dict
//   - Context fencing for memory recalls

/// <summary>
/// Manages plugin lifecycle: registration, initialization, hook dispatch,
/// tool routing, and graceful shutdown. All hook dispatches are failure-isolated —
/// one plugin throwing never blocks others or the agent.
/// </summary>
public sealed class PluginManager
{
    private readonly ILogger<PluginManager> _logger;
    private readonly List<IPlugin> _plugins = new List<IPlugin>();
    private readonly ConcurrentDictionary<string, IPlugin> _toolToPlugin = new();
    private readonly HashSet<string> _externalCategories = new HashSet<string>();
    private bool _initialized;

    public PluginManager(ILogger<PluginManager> logger)
    {
        _logger = logger;
    }

    /// <summary>All registered plugins.</summary>
    public IReadOnlyList<IPlugin> Plugins => _plugins;

    // ══════════════════════════════════════════
    // Registration
    // ══════════════════════════════════════════

    /// <summary>
    /// Register a plugin. Built-in plugins are always accepted.
    /// Only one external plugin per category is allowed (upstream constraint).
    /// </summary>
    public bool Register(IPlugin plugin)
    {
        if (!plugin.IsBuiltin)
        {
            if (_externalCategories.Contains(plugin.Category))
            {
                _logger.LogWarning(
                    "Rejected external plugin '{Name}' — category '{Category}' already has an external provider",
                    plugin.Name, plugin.Category);
                return false;
            }
            _externalCategories.Add(plugin.Category);
        }

        _plugins.Add(plugin);

        // Index tools for routing
        foreach (var tool in plugin.GetTools())
        {
            _toolToPlugin[tool.Name] = plugin;
            _logger.LogDebug("Routed tool '{Tool}' → plugin '{Plugin}'", tool.Name, plugin.Name);
        }

        _logger.LogInformation("Registered plugin: {Name} [{Category}] (builtin={Builtin})",
            plugin.Name, plugin.Category, plugin.IsBuiltin);
        return true;
    }

    /// <summary>Get all tools from all plugins for agent registration.</summary>
    public List<ITool> GetAllTools()
    {
        var tools = new List<ITool>();
        foreach (var plugin in _plugins)
        {
            try
            {
                tools.AddRange(plugin.GetTools());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Plugin '{Name}' failed to provide tools", plugin.Name);
            }
        }
        return tools;
    }

    /// <summary>Find which plugin owns a tool.</summary>
    public IPlugin? GetPluginForTool(string toolName) =>
        _toolToPlugin.TryGetValue(toolName, out var plugin) ? plugin : null;

    // ══════════════════════════════════════════
    // Initialization & Shutdown
    // ══════════════════════════════════════════

    public async Task InitializeAllAsync(PluginContext context, CancellationToken ct)
    {
        if (_initialized) return;
        _initialized = true;

        foreach (var plugin in _plugins)
        {
            try
            {
                await plugin.InitializeAsync(context, ct);
                _logger.LogInformation("Initialized plugin: {Name}", plugin.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize plugin '{Name}' — continuing without it", plugin.Name);
            }
        }
    }

    public async Task ShutdownAllAsync()
    {
        foreach (var plugin in _plugins)
        {
            try
            {
                await plugin.ShutdownAsync();
                _logger.LogDebug("Shut down plugin: {Name}", plugin.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Plugin '{Name}' threw during shutdown", plugin.Name);
            }
        }
    }

    // ══════════════════════════════════════════
    // Hook Dispatch (failure-isolated)
    // ══════════════════════════════════════════

    /// <summary>
    /// Collect system prompt blocks from all plugins.
    /// Memory context is fenced in &lt;memory-context&gt; tags (upstream pattern).
    /// </summary>
    public async Task<string> GetSystemPromptBlocksAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();

        foreach (var plugin in _plugins)
        {
            try
            {
                var block = await plugin.GetSystemPromptBlockAsync(ct);
                if (!string.IsNullOrWhiteSpace(block))
                {
                    if (plugin.Category == "memory")
                    {
                        // Context fencing — prevents model confusion with user input
                        sb.AppendLine("<memory-context>");
                        sb.AppendLine(block);
                        sb.AppendLine("</memory-context>");
                    }
                    else
                    {
                        sb.AppendLine(block);
                    }
                    sb.AppendLine();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Plugin '{Name}' failed in GetSystemPromptBlockAsync", plugin.Name);
            }
        }

        return sb.ToString();
    }

    public async Task OnTurnStartAsync(int turnNumber, string userMessage, CancellationToken ct)
    {
        foreach (var plugin in _plugins)
        {
            try { await plugin.OnTurnStartAsync(turnNumber, userMessage, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Plugin '{Name}' failed in OnTurnStart", plugin.Name); }
        }
    }

    public async Task OnTurnEndAsync(string userMessage, string assistantResponse, string sessionId, CancellationToken ct)
    {
        foreach (var plugin in _plugins)
        {
            try { await plugin.OnTurnEndAsync(userMessage, assistantResponse, sessionId, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Plugin '{Name}' failed in OnTurnEnd", plugin.Name); }
        }
    }

    public async Task OnSessionEndAsync(IReadOnlyList<Message> messages, CancellationToken ct)
    {
        foreach (var plugin in _plugins)
        {
            try { await plugin.OnSessionEndAsync(messages, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Plugin '{Name}' failed in OnSessionEnd", plugin.Name); }
        }
    }

    public async Task OnPreCompressAsync(IReadOnlyList<Message> messages, CancellationToken ct)
    {
        foreach (var plugin in _plugins)
        {
            try { await plugin.OnPreCompressAsync(messages, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Plugin '{Name}' failed in OnPreCompress", plugin.Name); }
        }
    }

    public async Task OnMemoryWriteAsync(string action, string target, string content, CancellationToken ct)
    {
        foreach (var plugin in _plugins)
        {
            try { await plugin.OnMemoryWriteAsync(action, target, content, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Plugin '{Name}' failed in OnMemoryWrite", plugin.Name); }
        }
    }

    public async Task OnDelegationCompleteAsync(string task, string result, string? childSessionId, CancellationToken ct)
    {
        foreach (var plugin in _plugins)
        {
            try { await plugin.OnDelegationCompleteAsync(task, result, childSessionId, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Plugin '{Name}' failed in OnDelegationComplete", plugin.Name); }
        }
    }
}
