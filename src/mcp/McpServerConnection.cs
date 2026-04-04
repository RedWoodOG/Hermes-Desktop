namespace Hermes.Agent.Mcp;

using System.Text.Json;

/// <summary>
/// Represents a connection to an MCP server with tool and resource discovery.
/// </summary>
public sealed class McpServerConnection : IAsyncDisposable
{
    private readonly McpServerConfig _config;
    private IMcpTransport? _transport;
    private readonly List<McpToolDefinition> _tools = new();
    private readonly List<McpResourceDefinition> _resources = new();
    private readonly CancellationTokenSource _notificationCts = new();
    private Task? _notificationTask;
    
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
    
    public string Name => _config.Name;
    public IReadOnlyList<McpToolDefinition> Tools => _tools;
    public IReadOnlyList<McpResourceDefinition> Resources => _resources;
    public bool IsConnected => _transport?.IsConnected ?? false;

    public McpServerConnection(McpServerConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Connect to the MCP server and discover tools/resources.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _transport = CreateTransport(_config);
        await _transport.ConnectAsync(ct);
        
        // Discover tools
        await DiscoverToolsAsync(ct);
        
        // Discover resources
        await DiscoverResourcesAsync(ct);
        
        // Start notification listener
        _notificationTask = Task.Run(() => ListenForNotifications(_notificationCts.Token), _notificationCts.Token);
    }
    
    private static IMcpTransport CreateTransport(McpServerConfig config)
    {
        return config switch
        {
            McpStdioConfig stdio => new StdioMcpTransport(stdio),
            McpHttpConfig http => new HttpSseMcpTransport(http),
            McpWebSocketConfig ws => new WebSocketMcpTransport(ws),
            _ => throw new ArgumentException($"Unknown config type: {config.GetType()}")
        };
    }
    
    private async Task DiscoverToolsAsync(CancellationToken ct)
    {
        if (_transport is null) return;
        
        try
        {
            var result = await _transport.SendRequestAsync("tools/list", null, ct);
            
            if (result.TryGetProperty("tools", out var toolsProp) && toolsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var toolElement in toolsProp.EnumerateArray())
                {
                    var tool = JsonSerializer.Deserialize<McpToolDefinition>(toolElement, JsonOptions);
                    if (tool is not null)
                    {
                        _tools.Add(tool);
                    }
                }
            }
        }
        catch (McpException ex) when (ex.Code == -32601) // Method not found
        {
            // Server doesn't support tools
        }
    }
    
    private async Task DiscoverResourcesAsync(CancellationToken ct)
    {
        if (_transport is null) return;
        
        try
        {
            var result = await _transport.SendRequestAsync("resources/list", null, ct);
            
            if (result.TryGetProperty("resources", out var resourcesProp) && resourcesProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var resourceElement in resourcesProp.EnumerateArray())
                {
                    var resource = JsonSerializer.Deserialize<McpResourceDefinition>(resourceElement, JsonOptions);
                    if (resource is not null)
                    {
                        _resources.Add(resource);
                    }
                }
            }
        }
        catch (McpException ex) when (ex.Code == -32601) // Method not found
        {
            // Server doesn't support resources
        }
    }
    
    private async Task ListenForNotifications(CancellationToken ct)
    {
        if (_transport is null) return;
        
        try
        {
            await foreach (var notification in _transport.Notifications.WithCancellation(ct))
            {
                // Handle tool list changed notification
                if (notification.Method == "notifications/tools/list_changed")
                {
                    await DiscoverToolsAsync(ct);
                }
                // Handle resource list changed notification
                else if (notification.Method == "notifications/resources/list_changed")
                {
                    await DiscoverResourcesAsync(ct);
                }
            }
        }
        catch (OperationCanceledException) { }
    }
    
    /// <summary>
    /// Call a tool on the MCP server.
    /// </summary>
    public async Task<McpToolResult> CallToolAsync(string toolName, JsonElement? arguments = null, CancellationToken ct = default)
    {
        if (_transport is null)
            throw new InvalidOperationException("Not connected to MCP server");
        
        var parameters = JsonSerializer.SerializeToElement(new
        {
            name = toolName,
            arguments = arguments
        });
        
        var result = await _transport.SendRequestAsync("tools/call", parameters, ct);
        
        return JsonSerializer.Deserialize<McpToolResult>(result, JsonOptions) 
            ?? new McpToolResult([]);
    }
    
    /// <summary>
    /// Read a resource from the MCP server.
    /// </summary>
    public async Task<McpResourceContent> ReadResourceAsync(string uri, CancellationToken ct = default)
    {
        if (_transport is null)
            throw new InvalidOperationException("Not connected to MCP server");
        
        var parameters = JsonSerializer.SerializeToElement(new { uri });
        var result = await _transport.SendRequestAsync("resources/read", parameters, ct);
        
        return JsonSerializer.Deserialize<McpResourceContent>(result, JsonOptions) 
            ?? new McpResourceContent([]);
    }
    
    /// <summary>
    /// Normalize tool name with server prefix: mcp__{server}__{tool}
    /// </summary>
    public string NormalizeToolName(string toolName) => $"mcp__{_config.Name}__{toolName}";
    
    /// <summary>
    /// Parse a normalized tool name back to (serverName, toolName).
    /// </summary>
    public static (string? serverName, string? toolName) ParseNormalizedToolName(string normalized)
    {
        var parts = normalized.Split("__");
        if (parts.Length == 3 && parts[0] == "mcp")
        {
            return (parts[1], parts[2]);
        }
        return (null, null);
    }

    public async ValueTask DisposeAsync()
    {
        _notificationCts.Cancel();
        
        try
        {
            if (_notificationTask is not null)
                await _notificationTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch { }
        
        if (_transport is not null)
        {
            await _transport.DisposeAsync();
        }
        
        _notificationCts.Dispose();
    }
}

/// <summary>
/// Result from calling an MCP tool.
/// </summary>
public sealed record McpToolResult(
    IReadOnlyList<McpContentBlock> Content,
    bool IsError = false);

/// <summary>
/// Content block in MCP tool result.
/// </summary>
public abstract record McpContentBlock
{
    public sealed record Text(string Value) : McpContentBlock;
    public sealed record Image(string Data, string MimeType) : McpContentBlock;
    public sealed record Resource(string Uri, string? MimeType = null, string? TextContent = null) : McpContentBlock;
}

/// <summary>
/// Resource content from MCP server.
/// </summary>
public sealed record McpResourceContent(
    IReadOnlyList<McpResourceBlock> Contents);

/// <summary>
/// Resource block in MCP resource content.
/// </summary>
public sealed record McpResourceBlock(
    string Uri,
    string? MimeType = null,
    string? Text = null);
