namespace Hermes.Agent.Mcp;

using System.Text.Json;

/// <summary>
/// Transport abstraction for MCP (Model Context Protocol) connections.
/// Supports stdio, HTTP/SSE, and WebSocket transports.
/// </summary>
public interface IMcpTransport : IAsyncDisposable
{
    /// <summary>
    /// Connect to the MCP server.
    /// </summary>
    Task ConnectAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Send a JSON-RPC request and await the response.
    /// </summary>
    Task<JsonElement> SendRequestAsync(string method, JsonElement? parameters = null, CancellationToken ct = default);
    
    /// <summary>
    /// Stream of notifications from the server.
    /// </summary>
    IAsyncEnumerable<McpNotification> Notifications { get; }
    
    /// <summary>
    /// Whether the transport is currently connected.
    /// </summary>
    bool IsConnected { get; }
}
