namespace Hermes.Agent.Permissions;

using Hermes.Agent.Tools;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

/// <summary>
/// Granular permission system with rule-based DSL.
/// Not binary (allow/deny) - supports allow, deny, ask with patterns.
/// </summary>

public sealed class PermissionManager
{
    private readonly PermissionContext _context;
    private readonly ILogger<PermissionManager> _logger;
    
    public PermissionManager(PermissionContext context, ILogger<PermissionManager> logger)
    {
        _context = context;
        _logger = logger;
    }
    
    /// <summary>
    /// Check permissions for a tool call.
    /// Returns Allow, Ask, or Deny decision.
    /// </summary>
    public async Task<PermissionDecision> CheckPermissionsAsync<T>(
        string toolName,
        T input,
        CancellationToken ct)
    {
        _logger.LogDebug("Checking permissions for {ToolName}", toolName);
        
        // 1. Check mode
        if (_context.Mode == PermissionMode.BypassPermissions)
        {
            _logger.LogDebug("Bypass mode: allowing {ToolName}", toolName);
            return Allow(input);
        }
        
        if (_context.Mode == PermissionMode.Plan)
        {
            if (IsReadOnlyTool(toolName, input))
            {
                _logger.LogDebug("Plan mode: allowing read-only {ToolName}", toolName);
                return Allow(input);
            }
            else
            {
                _logger.LogDebug("Plan mode: denying write operation {ToolName}", toolName);
                return Deny($"Cannot modify files in plan mode");
            }
        }
        
        // 2. Check always_allow rules
        if (MatchesRule(toolName, input, _context.AlwaysAllow))
        {
            _logger.LogDebug("Matched always_allow rule for {ToolName}", toolName);
            return Allow(input);
        }
        
        // 3. Check always_deny rules
        if (MatchesRule(toolName, input, _context.AlwaysDeny))
        {
            _logger.LogDebug("Matched always_deny rule for {ToolName}", toolName);
            return Deny($"Blocked by permission rule");
        }
        
        // 4. Check always_ask rules
        if (MatchesRule(toolName, input, _context.AlwaysAsk))
        {
            _logger.LogDebug("Matched always_ask rule for {ToolName}", toolName);
            return Ask($"Requires permission: {toolName}");
        }
        
        // 5. Default behavior by mode
        var decision = _context.Mode switch
        {
            PermissionMode.Auto => IsReadOnlyTool(toolName, input) 
                ? Allow(input, "Auto-approved read-only operation")
                : Ask($"Modify operation requires permission"),
            
            PermissionMode.AcceptEdits => IsInWorkspace(input)
                ? Allow(input, "Auto-approved: within workspace")
                : Ask($"Outside workspace, requires permission"),
            
            PermissionMode.Default => Ask($"Default: requires permission"),
            
            _ => Ask($"Unknown mode: {_context.Mode}")
        };
        
        _logger.LogDebug("Permission decision for {ToolName}: {Decision}", toolName, decision.Behavior);
        return decision;
    }
    
    private bool MatchesRule<T>(string toolName, T input, List<PermissionRule> rules)
    {
        foreach (var rule in rules)
        {
            // Check tool name match
            if (rule.ToolName != toolName && rule.ToolName != "*")
                continue;
            
            // If no pattern, tool name match is enough
            if (rule.Pattern == null)
                return true;
            
            // Check pattern against input
            if (MatchesPattern(input, rule.Pattern))
                return true;
        }
        
        return false;
    }
    
    private bool MatchesPattern<T>(T input, string pattern)
    {
        // Convert input to string for pattern matching
        var inputStr = input?.ToString() ?? "";
        
        // Simple glob pattern matching
        // Examples: "git *", "**/*.cs", "src/**"
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        
        return Regex.IsMatch(inputStr, regexPattern, RegexOptions.IgnoreCase);
    }
    
    private bool IsReadOnlyTool<T>(string toolName, T input)
    {
        // Read-only tools
        var readOnlyTools = new[] { "read_file", "glob", "grep", "ls", "task_get", "task_list", "terminal", "web_search", "web_fetch", "todo_write", "ask_user", "schedule_cron" };
        
        if (readOnlyTools.Contains(toolName))
            return true;
        
        // Check if bash command is read-only
        if (toolName == "bash" && input is BashParameters bash)
        {
            return IsReadOnlyBashCommand(bash.Command);
        }
        
        return false;
    }
    
    private bool IsReadOnlyBashCommand(string command)
    {
        // Read-only commands
        var readOnlyPrefixes = new[] {
            "git ", "ls ", "dir ", "cat ", "head ", "tail ", "grep ", "rg ", "find ",
            "pwd ", "echo ", "type ", "where ", "which ", "wc ", "du ", "df ",
            "dotnet --", "dotnet list", "node --", "python --version",
            "Get-ChildItem", "Get-Content", "Get-Item", "Get-Process",
            "Test-Path", "Resolve-Path"
        };
        return readOnlyPrefixes.Any(p => command.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }
    
    private bool IsInWorkspace<T>(T input)
    {
        // Check if path is within workspace
        // This is a simplified check - real implementation would validate paths
        return true; // TODO: Implement proper workspace checking
    }
    
    private static PermissionDecision Allow<T>(T input, string? reason = null) => new()
    {
        Behavior = PermissionBehavior.Allow,
        UpdatedInput = input,
        DecisionReason = reason
    };
    
    private static PermissionDecision Ask(string message) => new()
    {
        Behavior = PermissionBehavior.Ask,
        Message = message
    };
    
    private static PermissionDecision Deny(string message) => new()
    {
        Behavior = PermissionBehavior.Deny,
        Message = message
    };
}

/// <summary>
/// Permission context with rules and mode.
/// </summary>
public sealed class PermissionContext
{
    public PermissionMode Mode { get; set; } = PermissionMode.Default;
    public List<PermissionRule> AlwaysAllow { get; set; } = new();
    public List<PermissionRule> AlwaysDeny { get; set; } = new();
    public List<PermissionRule> AlwaysAsk { get; set; } = new();
    public List<string> AdditionalWorkingDirectories { get; set; } = new();
}

/// <summary>
/// Permission rule with tool name and optional pattern.
/// Examples:
/// - Bash(git *) - Allow all git commands
/// - Write(**/*.cs) - Allow writing C# files
/// - Read(src/**) - Allow reading from src/
/// - * - Match all tools
/// </summary>
public sealed class PermissionRule
{
    public string ToolName { get; set; } = "";
    public string? Pattern { get; set; }
    
    public static PermissionRule AllowAll(string toolName) => new() { ToolName = toolName };
    public static PermissionRule AllowPattern(string toolName, string pattern) => new() { ToolName = toolName, Pattern = pattern };
    public static PermissionRule DenyAll(string toolName) => new() { ToolName = toolName };
}

public enum PermissionMode
{
    /// <summary>Always ask for permission</summary>
    Default,
    
    /// <summary>Read-only planning mode, deny writes</summary>
    Plan,
    
    /// <summary>Auto-approve read-only, ask for writes</summary>
    Auto,
    
    /// <summary>Bypass all permission checks (dangerous!)</summary>
    BypassPermissions,
    
    /// <summary>Auto-approve edits within workspace</summary>
    AcceptEdits
}

public enum PermissionBehavior
{
    Allow,
    Ask,
    Deny
}

public sealed class PermissionDecision
{
    public PermissionBehavior Behavior { get; set; }
    public object? UpdatedInput { get; set; }
    public string? Message { get; set; }
    public string? DecisionReason { get; set; }
    
    public bool IsAllowed => Behavior == PermissionBehavior.Allow;
    public bool IsDenied => Behavior == PermissionBehavior.Deny;
    public bool NeedsUserInput => Behavior == PermissionBehavior.Ask;
}

/// <summary>
/// Permission request for user dialog.
/// </summary>
public sealed class PermissionRequest
{
    public required string ToolName { get; init; }
    public required object Input { get; init; }
    public required string Message { get; init; }
    public required string DecisionReason { get; init; }
}
