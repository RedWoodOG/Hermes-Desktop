namespace Hermes.Agent.Plugins;

using Hermes.Agent.Core;
using Hermes.Agent.Memory;
using System.Text;

/// <summary>
/// Built-in memory plugin adapter. Wraps the existing MemoryManager
/// as a plugin so it participates in the plugin lifecycle alongside
/// any external memory providers.
///
/// Upstream ref: agent/memory_manager.py "builtin" provider
/// </summary>
public sealed class BuiltinMemoryPlugin : PluginBase
{
    private readonly MemoryManager _memoryManager;
    private string? _lastQuery;

    public BuiltinMemoryPlugin(MemoryManager memoryManager)
    {
        _memoryManager = memoryManager;
    }

    public override string Name => "builtin-memory";
    public override bool IsBuiltin => true;
    public override string Category => "memory";

    public override async Task<string?> GetSystemPromptBlockAsync(CancellationToken ct)
    {
        if (_lastQuery is null) return null;

        var memories = await _memoryManager.LoadRelevantMemoriesAsync(_lastQuery, [], ct);
        if (memories.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("## Relevant Memories");
        foreach (var mem in memories)
        {
            sb.AppendLine($"### {mem.Filename}");
            if (mem.FreshnessWarning is not null)
                sb.AppendLine($"⚠️ {mem.FreshnessWarning}");
            sb.AppendLine(mem.Content);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public override Task OnTurnStartAsync(int turnNumber, string userMessage, CancellationToken ct)
    {
        // Cache the query for system prompt block retrieval
        _lastQuery = userMessage;
        return Task.CompletedTask;
    }

    public override Task OnTurnEndAsync(string userMessage, string assistantResponse, string sessionId, CancellationToken ct)
    {
        _lastQuery = null;
        return Task.CompletedTask;
    }
}
