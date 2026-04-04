namespace Hermes.Agent.Mcp;

using System.Text.Json;

/// <summary>
/// MCP notification received from server.
/// </summary>
public sealed record McpNotification(
    string Method,
    JsonElement? Params = null);

/// <summary>
/// MCP tool definition from server.
/// </summary>
public sealed record McpToolDefinition(
    string Name,
    string? Description = null,
    JsonElement? InputSchema = null);

/// <summary>
/// MCP resource definition from server.
/// </summary>
public sealed record McpResourceDefinition(
    string Uri,
    string Name,
    string? Description = null,
    string? MimeType = null);

/// <summary>
/// MCP server configuration.
/// </summary>
public abstract record McpServerConfig(string Name);

/// <summary>
/// MCP server using stdio transport (spawn process).
/// </summary>
public sealed record McpStdioConfig(
    string Name,
    string Command,
    IReadOnlyList<string>? Args = null,
    IReadOnlyDictionary<string, string>? Env = null,
    string? WorkingDirectory = null) : McpServerConfig(Name);

/// <summary>
/// MCP server using HTTP/SSE transport.
/// </summary>
public sealed record McpHttpConfig(
    string Name,
    Uri Url,
    IReadOnlyDictionary<string, string>? Headers = null) : McpServerConfig(Name);

/// <summary>
/// MCP server using WebSocket transport.
/// </summary>
public sealed record McpWebSocketConfig(
    string Name,
    Uri Url,
    IReadOnlyDictionary<string, string>? Headers = null) : McpServerConfig(Name);

/// <summary>
/// JSON-RPC request structure.
/// </summary>
public sealed record JsonRpcRequest(
    string JsonRpc = "2.0",
    string? Id = null,
    string? Method = null,
    JsonElement? Params = null);

/// <summary>
/// JSON-RPC response structure.
/// </summary>
public sealed record JsonRpcResponse(
    string JsonRpc = "2.0",
    string? Id = null,
    JsonElement? Result = null,
    JsonRpcError? Error = null);

/// <summary>
/// JSON-RPC error structure.
/// </summary>
public sealed record JsonRpcError(
    int Code,
    string Message,
    JsonElement? Data = null);
