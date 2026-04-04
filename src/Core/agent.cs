namespace Hermes.Agent.Core;

using Hermes.Agent.LLM;
using Microsoft.Extensions.Logging;
using System.Text.Json;

public sealed class Agent : IAgent
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<Agent> _logger;
    private readonly Dictionary<string, ITool> _tools = new();

    /// <summary>Safety limit to prevent infinite tool loops.</summary>
    public int MaxToolIterations { get; set; } = 25;

    public Agent(IChatClient chatClient, ILogger<Agent> logger)
    {
        _chatClient = chatClient;
        _logger = logger;
    }

    public void RegisterTool(ITool tool)
    {
        _tools[tool.Name] = tool;
        _logger.LogInformation("Registered tool: {ToolName}", tool.Name);
    }

    public IReadOnlyDictionary<string, ITool> Tools => _tools;

    /// <summary>Build ToolDefinition list from registered tools for the LLM.</summary>
    public List<ToolDefinition> GetToolDefinitions()
    {
        return _tools.Values.Select(t => new ToolDefinition
        {
            Name = t.Name,
            Description = t.Description,
            Parameters = BuildParameterSchema(t)
        }).ToList();
    }

    /// <summary>
    /// Full chat loop with tool calling. Sends the user message, then iterates:
    /// LLM responds → if tool calls, execute them → feed results back → repeat
    /// until LLM produces a final text response or we hit MaxToolIterations.
    /// </summary>
    public async Task<string> ChatAsync(string message, Session session, CancellationToken ct)
    {
        session.AddMessage(new Message { Role = "user", Content = message });
        _logger.LogInformation("Processing message for session {SessionId}", session.Id);

        if (_tools.Count == 0)
        {
            // No tools registered — simple completion
            var response = await _chatClient.CompleteAsync(session.Messages, ct);
            session.AddMessage(new Message { Role = "assistant", Content = response });
            return response;
        }

        var toolDefs = GetToolDefinitions();
        var iterations = 0;

        while (iterations < MaxToolIterations)
        {
            iterations++;
            var response = await _chatClient.CompleteWithToolsAsync(session.Messages, toolDefs, ct);

            if (!response.HasToolCalls)
            {
                // LLM is done — return final text
                var finalContent = response.Content ?? "";
                session.AddMessage(new Message { Role = "assistant", Content = finalContent });
                return finalContent;
            }

            // Record the assistant message with its tool call requests
            session.AddMessage(new Message
            {
                Role = "assistant",
                Content = response.Content ?? "",
                ToolCalls = response.ToolCalls
            });

            // Execute each tool call and append results
            foreach (var toolCall in response.ToolCalls!)
            {
                _logger.LogInformation("Executing tool {ToolName} (call {CallId})", toolCall.Name, toolCall.Id);
                var result = await ExecuteToolCallAsync(toolCall, ct);
                session.AddMessage(new Message
                {
                    Role = "tool",
                    Content = result.Content,
                    ToolCallId = toolCall.Id,
                    ToolName = toolCall.Name
                });
            }
        }

        _logger.LogWarning("Hit max tool iterations ({Max}) for session {SessionId}", MaxToolIterations, session.Id);
        var fallback = "I've reached the maximum number of tool call iterations. Here's what I've accomplished so far based on the conversation above.";
        session.AddMessage(new Message { Role = "assistant", Content = fallback });
        return fallback;
    }

    private async Task<ToolResult> ExecuteToolCallAsync(ToolCall toolCall, CancellationToken ct)
    {
        if (!_tools.TryGetValue(toolCall.Name, out var tool))
        {
            _logger.LogWarning("Unknown tool requested: {ToolName}", toolCall.Name);
            return ToolResult.Fail($"Unknown tool: {toolCall.Name}");
        }

        try
        {
            var parameters = JsonSerializer.Deserialize(toolCall.Arguments, tool.ParametersType, ToolArgJsonOptions)
                ?? throw new JsonException($"Failed to deserialize arguments for {toolCall.Name}");
            return await tool.ExecuteAsync(parameters, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool {ToolName} failed", toolCall.Name);
            return ToolResult.Fail($"Tool execution failed: {ex.Message}", ex);
        }
    }

    private static JsonElement BuildParameterSchema(ITool tool)
    {
        // Build a JSON Schema from the tool's ParametersType using reflection
        var props = tool.ParametersType.GetProperties();
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var prop in props)
        {
            var jsonType = prop.PropertyType switch
            {
                Type t when t == typeof(string) => "string",
                Type t when t == typeof(int) || t == typeof(long) => "integer",
                Type t when t == typeof(double) || t == typeof(float) => "number",
                Type t when t == typeof(bool) => "boolean",
                Type t when t.IsArray || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) => "array",
                _ => "string"
            };

            var propSchema = new Dictionary<string, object> { ["type"] = jsonType };

            // Check for Description attribute or XML doc
            var descAttr = prop.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
                .FirstOrDefault() as System.ComponentModel.DescriptionAttribute;
            if (descAttr is not null)
                propSchema["description"] = descAttr.Description;

            properties[ToCamelCase(prop.Name)] = propSchema;

            // Non-nullable value types and required strings are required
            if (prop.PropertyType.IsValueType && Nullable.GetUnderlyingType(prop.PropertyType) is null)
                required.Add(ToCamelCase(prop.Name));
        }

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties
        };
        if (required.Count > 0)
            schema["required"] = required;

        var json = JsonSerializer.Serialize(schema);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static readonly JsonSerializerOptions ToolArgJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static string ToCamelCase(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
