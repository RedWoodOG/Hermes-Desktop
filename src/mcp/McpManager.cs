namespace Hermes.Agent.Mcp;

using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// Manages multiple MCP server connections and provides unified tool discovery.
/// </summary>
public sealed class McpManager : IAsyncDisposable
{
    private readonly ILogger<McpManager> _logger;
    private readonly Dictionary<string, McpServerConnection> _connections = new();
    private readonly Dictionary<string, McpToolWrapper> _tools = new();
    private readonly List<McpServerConfig> _configs = new();
    
    public IReadOnlyDictionary<string, McpServerConnection> Connections => _connections;
    public IReadOnlyDictionary<string, McpToolWrapper> Tools => _tools;
    
    public McpManager(ILogger<McpManager> logger)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// Load MCP server configurations from config file.
    /// </summary>
    public async Task LoadFromConfigAsync(string configPath, CancellationToken ct = default)
    {
        if (!File.Exists(configPath))
        {
            _logger.LogDebug("MCP config file not found: {Path}", configPath);
            return;
        }
        
        var json = await File.ReadAllTextAsync(configPath, ct);
        var config = JsonSerializer.Deserialize<McpConfigFile>(json);
        
        if (config?.McpServers is null) return;
        
        foreach (var (name, serverConfig) in config.McpServers)
        {
            McpServerConfig? mcpConfig = null;
            
            if (serverConfig.Command is not null)
            {
                mcpConfig = new McpStdioConfig(
                    name,
                    serverConfig.Command,
                    serverConfig.Args,
                    serverConfig.Env
                );
            }
            else if (serverConfig.Url is not null)
            {
                mcpConfig = new McpHttpConfig(name, new Uri(serverConfig.Url), serverConfig.Headers);
            }
            
            if (mcpConfig is not null)
            {
                _configs.Add(mcpConfig);
            }
        }
    }
    
    /// <summary>
    /// Add a server configuration programmatically.
    /// </summary>
    public void AddServer(McpServerConfig config)
    {
        _configs.Add(config);
    }
    
    /// <summary>
    /// Connect to all configured MCP servers.
    /// </summary>
    public async Task ConnectAllAsync(CancellationToken ct = default)
    {
        var tasks = _configs.Select(c => ConnectServerAsync(c, ct)).ToList();
        await Task.WhenAll(tasks);
    }
    
    private async Task ConnectServerAsync(McpServerConfig config, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Connecting to MCP server: {Name}", config.Name);
            
            var connection = new McpServerConnection(config);
            await connection.ConnectAsync(ct);
            
            _connections[config.Name] = connection;
            
            // Register tools
            foreach (var tool in connection.Tools)
            {
                var wrapper = new McpToolWrapper(connection, tool);
                _tools[wrapper.Name] = wrapper;
                _logger.LogDebug("Registered MCP tool: {Name}", wrapper.Name);
            }
            
            _logger.LogInformation("Connected to MCP server {Name} with {Count} tools", 
                config.Name, connection.Tools.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to MCP server: {Name}", config.Name);
        }
    }
    
    /// <summary>
    /// Get a tool by normalized name.
    /// </summary>
    public McpToolWrapper? GetTool(string normalizedName)
    {
        return _tools.TryGetValue(normalizedName, out var tool) ? tool : null;
    }
    
    /// <summary>
    /// Get all tool names (normalized).
    /// </summary>
    public IEnumerable<string> GetToolNames() => _tools.Keys;
    
    /// <summary>
    /// Disconnect a specific server.
    /// </summary>
    public async Task DisconnectAsync(string name, CancellationToken ct = default)
    {
        if (_connections.TryGetValue(name, out var connection))
        {
            // Remove tools from this server
            var toolsToRemove = _tools
                .Where(kvp => kvp.Value.Name.StartsWith($"mcp__{name}__"))
                .Select(kvp => kvp.Key)
                .ToList();
            
            foreach (var toolName in toolsToRemove)
            {
                _tools.Remove(toolName);
            }
            
            _connections.Remove(name);
            await connection.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections.Values)
        {
            await connection.DisposeAsync();
        }
        
        _connections.Clear();
        _tools.Clear();
    }
}

/// <summary>
/// MCP configuration file format.
/// </summary>
public sealed class McpConfigFile
{
    public Dictionary<string, McpServerConfigEntry>? McpServers { get; init; }
}

/// <summary>
/// MCP server configuration entry from config file.
/// </summary>
public sealed class McpServerConfigEntry
{
    public string? Command { get; init; }
    public IReadOnlyList<string>? Args { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    public string? Url { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}
