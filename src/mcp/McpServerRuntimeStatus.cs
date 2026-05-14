namespace Hermes.Agent.Mcp;

/// <summary>Per-server MCP status for dashboards (no secrets).</summary>
public sealed record McpServerRuntimeStatus(
    string Name,
    string TransportLabel,
    bool IsConnected,
    int RegisteredToolCount);

/// <summary>Configuration entry that was skipped (e.g. policy / parse).</summary>
public sealed record McpConfigLoadIssue(string ServerName, string Reason);
