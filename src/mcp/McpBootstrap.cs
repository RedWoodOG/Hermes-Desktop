namespace Hermes.Agent.Mcp;

using Hermes.Agent.Core;
using Hermes.Agent.Tools;
using Microsoft.Extensions.Logging;

/// <summary>
/// Starts the MCP host and registers discovered MCP tools with the Hermes tool registries.
/// </summary>
public static class McpBootstrap
{
    public static async Task<int> AttachAsync(
        McpManager manager,
        Agent agent,
        IToolRegistry toolRegistry,
        IEnumerable<string> configPaths,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(configPaths);

        // Snapshot the (raw, ordered) search path list onto the manager so the desktop's
        // McpPage dashboard can show *exactly* what bootstrap inspected — never a list it
        // rebuilt with a potentially-different projectDir.
        var configPathList = configPaths as IReadOnlyList<string> ?? configPaths.ToList();
        manager.RecordBootstrapConfigSearchPaths(configPathList);

        var existingConfigs = configPathList
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .ToList();

        if (existingConfigs.Count == 0)
        {
            logger.LogInformation("No mcp.json found in configured search paths; MCP disabled.");
            return 0;
        }

        manager.PrepareForBootstrapAttach();

        foreach (var configPath in existingConfigs)
        {
            logger.LogInformation("Loading MCP config: {Path}", configPath);
            await manager.LoadFromConfigAsync(configPath, ct).ConfigureAwait(false);
        }

        await manager.ConnectAllAsync(ct).ConfigureAwait(false);

        foreach (var tool in manager.Tools.Values)
        {
            agent.RegisterTool(tool);
            toolRegistry.RegisterTool(tool);
        }

        logger.LogInformation(
            "MCP attached: {Servers} connected server(s), {Tools} tool(s) registered.",
            manager.ServerCount,
            manager.Tools.Count);

        return manager.Tools.Count;
    }

    /// <summary>Standard <c>mcp.json</c> search order (matches desktop bootstrap).</summary>
    public static IReadOnlyList<string> BuildMcpConfigSearchPaths(string hermesProjectCsDir, string hermesHomePath) =>
        new[]
        {
            Path.Combine(hermesProjectCsDir, "mcp.json"),
            Path.Combine(hermesHomePath, "mcp.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hermes", "mcp.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hermes", "mcp.json"),
        };
}
