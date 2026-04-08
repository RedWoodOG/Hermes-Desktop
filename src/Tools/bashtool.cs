namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using Hermes.Agent.Execution;
using Hermes.Agent.Security;

/// <summary>
/// Bash/PowerShell shell command execution tool.
/// Routes through execution backends (Local, Docker, SSH, Modal, Daytona).
/// Supports timeout, background execution, sandbox enforcement, and security validation.
/// </summary>
public sealed class BashTool : ITool
{
    private readonly ShellSecurityAnalyzer _securityAnalyzer;
    private readonly BashSecurityPolicy? _policy;
    private IExecutionBackend _backend;

    public string Name => "bash";
    public string Description => "Execute shell commands with timeout, background support, security validation. Supports local, Docker, SSH, Modal, and Daytona backends.";
    public Type ParametersType => typeof(BashParameters);

    public BashTool(BashSecurityPolicy? policy = null, ExecutionConfig? executionConfig = null)
    {
        _securityAnalyzer = new ShellSecurityAnalyzer();
        _policy = policy;
        _backend = ExecutionBackendFactory.Create(executionConfig ?? new ExecutionConfig());
    }

    /// <summary>Switch execution backend at runtime (e.g., from Settings).</summary>
    public void SetBackend(ExecutionConfig config) =>
        _backend = ExecutionBackendFactory.Create(config);

    public async Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (BashParameters)parameters;

        // Security analysis — always enforced, never skippable
        var context = new ShellContext
        {
            WorkingDirectory = p.WorkingDirectory,
            IsSandboxed = _policy?.IsSandboxed ?? false,
            AllowNetwork = _policy?.AllowNetwork ?? true,
            AllowFileSystemWrite = _policy?.AllowFileSystemWrite ?? true,
            AllowSubprocess = _policy?.AllowSubprocess ?? true,
        };

        var securityResult = _securityAnalyzer.Analyze(p.Command, context);

        switch (securityResult.Classification)
        {
            case SecurityClassification.Dangerous:
                return ToolResult.Fail($"Security: {securityResult.Reason}");

            case SecurityClassification.TooComplex:
                return ToolResult.Fail($"Security: Command too complex to analyze - {securityResult.Reason}");

            case SecurityClassification.NeedsReview:
                if (_policy?.AutoApproveWarnings != true)
                {
                    var warnings = string.Join("\n", securityResult.Warnings ?? new List<string> { securityResult.Reason ?? "Unknown" });
                    return ToolResult.Fail($"Security review required:\n{warnings}");
                }
                break;
        }

        // Execute via backend
        try
        {
            var result = await _backend.ExecuteAsync(
                p.Command, p.WorkingDirectory, p.TimeoutMs, p.RunInBackground, ct);

            if (result.Success)
            {
                var output = result.Output;
                if (result.ExitCodeMeaning is not null)
                    output += $"\n({result.ExitCodeMeaning})";
                if (result.BackgroundProcessId is not null)
                    output += $"\nBackground PID: {result.BackgroundProcessId}";
                return ToolResult.Ok(output);
            }
            else
            {
                var msg = $"Exit code {result.ExitCode}: {result.Output}";
                if (result.ExitCodeMeaning is not null)
                    msg += $"\n({result.ExitCodeMeaning})";
                return ToolResult.Fail(msg);
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to execute command: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// Security policy for bash execution.
/// </summary>
public sealed class BashSecurityPolicy
{
    public bool IsSandboxed { get; init; }
    public bool AllowNetwork { get; init; } = true;
    public bool AllowFileSystemWrite { get; init; } = true;
    public bool AllowSubprocess { get; init; } = true;
    public bool AutoApproveWarnings { get; init; } = false;
}

public sealed class BashParameters
{
    public required string Command { get; init; }
    public string? WorkingDirectory { get; init; }
    public int TimeoutMs { get; init; } = 120000;
    public bool RunInBackground { get; init; }
    public string? Description { get; init; }
    // Security check is always enforced — cannot be bypassed via parameters
}
