namespace Hermes.Agent.Permissions;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// Persists permission rules scoped to a specific workspace.
/// </summary>
public sealed class WorkspacePermissionRuleStore
{
    private readonly string _workspacePermissionsDir;
    private readonly string _workspacePath;
    private readonly string _workspaceKey;
    private readonly string _workspaceFilePath;
    private readonly ILogger<WorkspacePermissionRuleStore> _logger;

    public WorkspacePermissionRuleStore(
        string workspacePermissionsDir,
        string workspacePath,
        ILogger<WorkspacePermissionRuleStore> logger)
    {
        if (string.IsNullOrWhiteSpace(workspacePermissionsDir))
            throw new ArgumentException("Permissions directory is required.", nameof(workspacePermissionsDir));
        if (string.IsNullOrWhiteSpace(workspacePath))
            throw new ArgumentException("Workspace path is required.", nameof(workspacePath));

        _workspacePermissionsDir = workspacePermissionsDir;
        _workspacePath = NormalizeWorkspacePath(workspacePath);
        _workspaceKey = BuildWorkspaceKey(_workspacePath);
        _workspaceFilePath = Path.Combine(_workspacePermissionsDir, $"{_workspaceKey}.json");
        _logger = logger;
    }

    public string WorkspaceFilePath => _workspaceFilePath;

    public IReadOnlyList<PermissionRule> LoadAlwaysAllowRules()
    {
        if (!File.Exists(_workspaceFilePath))
            return Array.Empty<PermissionRule>();

        try
        {
            var json = File.ReadAllText(_workspaceFilePath);
            var payload = JsonSerializer.Deserialize<WorkspacePermissionRulePayload>(json);
            if (payload?.AlwaysAllow is null || payload.AlwaysAllow.Count == 0)
                return Array.Empty<PermissionRule>();

            var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var output = new List<PermissionRule>();
            foreach (var item in payload.AlwaysAllow)
            {
                if (string.IsNullOrWhiteSpace(item.ToolName))
                    continue;

                var normalizedTool = item.ToolName.Trim();
                var normalizedPattern = string.IsNullOrWhiteSpace(item.Pattern) ? null : item.Pattern.Trim();
                var dedupeKey = $"{normalizedTool}\n{normalizedPattern ?? string.Empty}";
                if (!dedupe.Add(dedupeKey))
                    continue;

                output.Add(new PermissionRule
                {
                    ToolName = normalizedTool,
                    Pattern = normalizedPattern
                });
            }

            return output;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed loading workspace permission rules from {Path}. Continuing with empty rules.",
                _workspaceFilePath);
            return Array.Empty<PermissionRule>();
        }
    }

    public void SaveAlwaysAllowRules(IEnumerable<PermissionRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<PermissionRuleItem>();
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.ToolName))
                continue;

            var toolName = rule.ToolName.Trim();
            var pattern = string.IsNullOrWhiteSpace(rule.Pattern) ? null : rule.Pattern.Trim();
            var dedupeKey = $"{toolName}\n{pattern ?? string.Empty}";
            if (!dedupe.Add(dedupeKey))
                continue;

            normalized.Add(new PermissionRuleItem
            {
                ToolName = toolName,
                Pattern = pattern
            });
        }

        var payload = new WorkspacePermissionRulePayload
        {
            WorkspacePath = _workspacePath,
            AlwaysAllow = normalized
        };

        try
        {
            Directory.CreateDirectory(_workspacePermissionsDir);

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            var tempPath = $"{_workspaceFilePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _workspaceFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed saving workspace permission rules to {Path}.",
                _workspaceFilePath);
        }
    }

    public void ClearAlwaysAllowRules()
    {
        try
        {
            if (File.Exists(_workspaceFilePath))
                File.Delete(_workspaceFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed clearing workspace permission rules at {Path}.",
                _workspaceFilePath);
        }
    }

    private static string NormalizeWorkspacePath(string workspacePath)
    {
        return Path.GetFullPath(workspacePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string BuildWorkspaceKey(string normalizedWorkspacePath)
    {
        var bytes = Encoding.UTF8.GetBytes(normalizedWorkspacePath);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class WorkspacePermissionRulePayload
    {
        public string WorkspacePath { get; set; } = string.Empty;
        public List<PermissionRuleItem> AlwaysAllow { get; set; } = new();
    }

    private sealed class PermissionRuleItem
    {
        public string ToolName { get; set; } = string.Empty;
        public string? Pattern { get; set; }
    }
}
