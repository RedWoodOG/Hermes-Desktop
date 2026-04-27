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
        var existingConfigs = configPaths
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
}
