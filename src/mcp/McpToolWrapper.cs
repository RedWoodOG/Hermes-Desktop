namespace Hermes.Agent.Mcp;

using System.Text.Json;
using Hermes.Agent.Core;

/// <summary>
/// Wraps an MCP tool as an ITool for integration with the existing tool system.
/// </summary>
public sealed class McpToolWrapper : ITool
{
    private readonly McpServerConnection _connection;
    private readonly McpToolDefinition _definition;
    private readonly string _normalizedName;
    
    public string Name => _normalizedName;
    public string Description => _definition.Description ?? $"MCP tool: {_definition.Name}";
    public Type ParametersType => typeof(McpToolParameters);
    
    public McpToolWrapper(McpServerConnection connection, McpToolDefinition definition)
    {
        _connection = connection;
        _definition = definition;
        _normalizedName = connection.NormalizeToolName(definition.Name);
    }
    
    public async Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (McpToolParameters)parameters;
        
        try
        {
            var result = await _connection.CallToolAsync(_definition.Name, p.Arguments, ct);
            
            if (result.IsError)
            {
                var errorText = result.Content.FirstOrDefault(c => c is McpContentBlock.Text) as McpContentBlock.Text;
                return ToolResult.Fail(errorText?.Value ?? "MCP tool returned error");
            }
            
            // Format output
            var output = FormatContent(result.Content);
            return ToolResult.Ok(output);
        }
        catch (McpException ex)
        {
            return ToolResult.Fail($"MCP error: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to call MCP tool: {ex.Message}", ex);
        }
    }
    
    private static string FormatContent(IReadOnlyList<McpContentBlock> content)
    {
        var sb = new System.Text.StringBuilder();
        
        foreach (var block in content)
        {
            switch (block)
            {
                case McpContentBlock.Text text:
                    sb.AppendLine(text.Value);
                    break;
                    
                case McpContentBlock.Image img:
                    sb.AppendLine($"[Image: {img.MimeType}, {img.Data.Length} chars base64]");
                    break;
                    
                case McpContentBlock.Resource res:
                    sb.AppendLine($"[Resource: {res.Uri}{(res.MimeType is not null ? $" ({res.MimeType})" : "")}]");
                    if (res.TextContent is not null)
                        sb.AppendLine(res.TextContent);
                    break;
            }
        }
        
        return sb.ToString().TrimEnd();
    }
    
    /// <summary>
    /// Get the JSON schema for the tool parameters.
    /// </summary>
    public JsonElement? GetInputSchema() => _definition.InputSchema;
}

/// <summary>
/// Parameters for MCP tool invocation.
/// </summary>
public sealed class McpToolParameters
{
    /// <summary>
    /// JSON object containing tool arguments.
    /// </summary>
    public JsonElement? Arguments { get; init; }
    
    /// <summary>
    /// Create from a dictionary.
    /// </summary>
    public static McpToolParameters FromDictionary(IReadOnlyDictionary<string, object> args)
    {
        return new McpToolParameters
        {
            Arguments = JsonSerializer.SerializeToElement(args)
        };
    }
    
    /// <summary>
    /// Create from an object (will be serialized to JSON).
    /// </summary>
    public static McpToolParameters FromObject(object args)
    {
        return new McpToolParameters
        {
            Arguments = JsonSerializer.SerializeToElement(args)
        };
    }
}
