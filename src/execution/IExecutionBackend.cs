namespace Hermes.Agent.Execution;

using System.Text.Json.Serialization;

// ══════════════════════════════════════════════
// Execution Backend Interface
// ══════════════════════════════════════════════
//
// Upstream ref: tools/terminal_tool.py + tools/environments/
// 6 backends: Local, Docker, SSH, Singularity, Modal, Daytona
// Key patterns: backend factory via env var, output truncation
// (40% head + 60% tail), workdir validation, exit code interpretation

/// <summary>
/// Supported execution backends.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExecutionBackendType
{
    Local,
    Docker,
    Ssh,
    Singularity,
    Modal,
    Daytona
}

/// <summary>
/// Result of executing a command on a backend.
/// </summary>
public sealed class ExecutionResult
{
    public required string Output { get; init; }
    public required int ExitCode { get; init; }
    public string? Error { get; init; }
    public string? ExitCodeMeaning { get; init; }
    public long DurationMs { get; init; }
    public string? BackgroundProcessId { get; init; }

    public bool Success => ExitCode == 0;
}

/// <summary>
/// Configuration for an execution backend.
/// </summary>
public sealed class ExecutionConfig
{
    public ExecutionBackendType Backend { get; set; } = ExecutionBackendType.Local;

    // Docker
    public string? DockerImage { get; set; }
    public List<string> DockerVolumes { get; set; } = new List<string>();
    public Dictionary<string, string> DockerEnv { get; set; } = new();

    // SSH
    public string? SshHost { get; set; }
    public string? SshUser { get; set; }
    public int SshPort { get; set; } = 22;
    public string? SshKeyPath { get; set; }

    // Modal
    public string? ModalAppName { get; set; }

    // Daytona
    public string? DaytonaApiKey { get; set; }
    public string? DaytonaWorkspaceId { get; set; }

    // General
    public int DefaultTimeoutMs { get; set; } = 180_000; // 3 min
    public int MaxOutputChars { get; set; } = 50_000;
}

/// <summary>
/// Interface for command execution backends.
/// Each backend implements how to run a command in its environment.
/// </summary>
public interface IExecutionBackend : IAsyncDisposable
{
    ExecutionBackendType Type { get; }

    /// <summary>
    /// Execute a command and return the result.
    /// Output is truncated per upstream pattern (40% head, 60% tail).
    /// </summary>
    Task<ExecutionResult> ExecuteAsync(
        string command,
        string? workingDirectory = null,
        int? timeoutMs = null,
        bool background = false,
        CancellationToken ct = default);

    /// <summary>Optional cleanup (e.g., remove Docker container).</summary>
    Task CleanupAsync() => Task.CompletedTask;
}

/// <summary>
/// Factory that creates the appropriate backend based on configuration.
/// Upstream ref: tools/terminal_tool.py _create_environment()
/// </summary>
public static class ExecutionBackendFactory
{
    public static IExecutionBackend Create(ExecutionConfig config)
    {
        return config.Backend switch
        {
            ExecutionBackendType.Docker => new DockerBackend(config),
            ExecutionBackendType.Ssh => new SshBackend(config),
            ExecutionBackendType.Modal => new ModalBackend(config),
            ExecutionBackendType.Daytona => new DaytonaBackend(config),
            _ => new LocalBackend(config)
        };
    }
}

/// <summary>
/// Output truncation matching upstream pattern.
/// Keeps 40% head + 60% tail with truncation notice.
/// </summary>
public static class OutputTruncator
{
    public static string Truncate(string output, int maxChars)
    {
        if (output.Length <= maxChars) return output;

        var headSize = (int)(maxChars * 0.4);
        var tailSize = maxChars - headSize;
        var omitted = output.Length - headSize - tailSize;

        return string.Concat(
            output.AsSpan(0, headSize),
            $"\n\n... [OUTPUT TRUNCATED — {omitted} chars omitted] ...\n\n",
            output.AsSpan(output.Length - tailSize));
    }
}

/// <summary>
/// Exit code interpretation for common CLI tools.
/// Upstream ref: tools/terminal_tool.py _interpret_exit_code()
/// </summary>
public static class ExitCodeInterpreter
{
    public static string? Interpret(string command, int exitCode)
    {
        if (exitCode == 0) return null;
        if (exitCode == 124) return "Command timed out";

        var cmd = command.TrimStart().Split(' ', 2)[0].ToLowerInvariant();
        return (cmd, exitCode) switch
        {
            ("grep", 1) => "No matches found (not an error)",
            ("diff", 1) => "Files differ (expected, not an error)",
            ("git", 1) when command.Contains("diff") => "Changes detected (expected)",
            ("git", 1) when command.Contains("grep") => "No matches in git history",
            ("test", 1) or ("[", 1) => "Test condition was false",
            _ => null
        };
    }
}
