namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using System.Runtime.CompilerServices;
using System.Text.Json;

/// <summary>
/// Tool for spawning subagents with isolated context.
/// </summary>
public sealed class AgentTool : ITool
{
    private readonly IChatClient _chatClient;
    private readonly IToolRegistry _toolRegistry;
    private readonly AgentToolConfig _config;
    
    public string Name => "agent";
    public string Description => "Spawn a subagent to handle a specialized task with isolated context";
    public Type ParametersType => typeof(AgentParameters);
    
    public AgentTool(IChatClient chatClient, IToolRegistry toolRegistry, AgentToolConfig? config = null)
    {
        _chatClient = chatClient;
        _toolRegistry = toolRegistry;
        _config = config ?? new AgentToolConfig();
    }
    
    public async Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (AgentParameters)parameters;
        
        try
        {
            var output = new System.Text.StringBuilder();
            output.AppendLine($"[Agent: {p.AgentType}] Started");
            output.AppendLine($"Task: {p.Task}");
            output.AppendLine("---");
            
            // Build agent definition
            var definition = GetAgentDefinition(p.AgentType);
            
            // Build messages
            var messages = new List<Message>
            {
                new() { Role = "user", Content = p.Task }
            };
            
            // Run agent with streaming
            await foreach (var evt in _chatClient.StreamAsync(definition.SystemPrompt, messages, GetToolsForAgent(p.AgentType), ct))
            {
                switch (evt)
                {
                    case StreamEvent.TokenDelta delta:
                        output.Append(delta.Text);
                        break;
                        
                    case StreamEvent.ToolUseStart toolStart:
                        output.AppendLine($"\n[Tool: {toolStart.Name}]");
                        break;
                        
                    case StreamEvent.MessageComplete complete:
                        output.AppendLine($"\n---");
                        output.AppendLine($"[Agent: {p.AgentType}] Completed ({complete.StopReason})");
                        break;
                        
                    case StreamEvent.StreamError error:
                        output.AppendLine($"\n[Error: {error.Error.Message}]");
                        break;
                }
            }
            
            return ToolResult.Ok(output.ToString());
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail("Agent execution cancelled");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Agent execution failed: {ex.Message}", ex);
        }
    }
    
    private AgentDefinition GetAgentDefinition(string agentType)
    {
        return agentType.ToLowerInvariant() switch
        {
            "researcher" => new AgentDefinition(
                "Researcher",
                "You are a research specialist. Find and synthesize information from multiple sources. Be thorough and cite sources.",
                new[] { "webfetch", "websearch", "read_file", "glob", "grep" }
            ),
            
            "coder" => new AgentDefinition(
                "Coder",
                "You are a coding specialist. Write clean, efficient, well-documented code. Follow best practices and test your solutions.",
                new[] { "read_file", "write_file", "edit_file", "glob", "grep", "bash" }
            ),
            
            "analyst" => new AgentDefinition(
                "Analyst",
                "You are an analysis specialist. Break down complex problems, identify patterns, and provide actionable insights.",
                new[] { "read_file", "glob", "grep", "bash" }
            ),
            
            "planner" => new AgentDefinition(
                "Planner",
                "You are a planning specialist. Create detailed, actionable plans with clear steps, dependencies, and success criteria.",
                new[] { "read_file", "glob", "grep" }
            ),
            
            "reviewer" => new AgentDefinition(
                "Reviewer",
                "You are a code review specialist. Identify issues, suggest improvements, and ensure code quality and security.",
                new[] { "read_file", "glob", "grep", "bash" }
            ),
            
            _ => new AgentDefinition(
                "General",
                "You are a helpful assistant. Complete the task efficiently and accurately.",
                new[] { "read_file", "write_file", "edit_file", "glob", "grep", "bash", "webfetch", "websearch" }
            )
        };
    }
    
    private IEnumerable<ToolDefinition>? GetToolsForAgent(string agentType)
    {
        var definition = GetAgentDefinition(agentType);
        var tools = new List<ToolDefinition>();
        
        foreach (var toolName in definition.AllowedTools)
        {
            var tool = _toolRegistry.GetTool(toolName);
            if (tool is not null)
            {
                // Create tool definition from registered tool
                tools.Add(new ToolDefinition
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    Parameters = GetToolSchema(tool)
                });
            }
        }
        
        return tools.Count > 0 ? tools : null;
    }
    
    private static JsonElement GetToolSchema(ITool tool)
    {
        // Basic schema - in production would be more detailed
        return JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { },
            required = new string[] { }
        });
    }
}

/// <summary>
/// Definition of an agent type.
/// </summary>
public sealed record AgentDefinition(
    string Name,
    string SystemPrompt,
    IReadOnlyList<string> AllowedTools);

/// <summary>
/// Configuration for agent tool.
/// </summary>
public sealed class AgentToolConfig
{
    public int MaxSubagentDepth { get; init; } = 3;
    public int MaxTokensPerSubagent { get; init; } = 8000;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed class AgentParameters
{
    public required string AgentType { get; init; }
    public required string Task { get; init; }
    public string? Context { get; init; }
    public int MaxSteps { get; init; } = 10;
}

/// <summary>
/// Tool registry interface for discovering available tools.
/// </summary>
public interface IToolRegistry
{
    ITool? GetTool(string name);
    IEnumerable<ITool> GetAllTools();
    void RegisterTool(ITool tool);
}

/// <summary>
/// Default tool registry implementation.
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);
    
    public ITool? GetTool(string name) => _tools.TryGetValue(name, out var tool) ? tool : null;
    
    public IEnumerable<ITool> GetAllTools() => _tools.Values;
    
    public void RegisterTool(ITool tool) => _tools[tool.Name] = tool;
}
