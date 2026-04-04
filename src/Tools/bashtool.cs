namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using Hermes.Agent.Security;
using System.Diagnostics;
using System.Text.RegularExpressions;

/// <summary>
/// Bash/PowerShell shell command execution tool.
/// Supports timeout, background execution, sandbox enforcement, and security validation.
/// </summary>
public sealed class BashTool : ITool
{
    private readonly ShellSecurityAnalyzer _securityAnalyzer;
    private readonly BashSecurityPolicy? _policy;
    
    public string Name => "bash";
    public string Description => "Execute shell commands in a sandboxed environment with timeout, background support, and security validation";
    public Type ParametersType => typeof(BashParameters);
    
    public BashTool(BashSecurityPolicy? policy = null)
    {
        _securityAnalyzer = new ShellSecurityAnalyzer();
        _policy = policy;
    }
    
    public Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (BashParameters)parameters;
        return ExecuteCommandAsync(p.Command, p.WorkingDirectory, p.TimeoutMs, p.RunInBackground, p.Description, p.SkipSecurityCheck, ct);
    }
    
    private async Task<ToolResult> ExecuteCommandAsync(
        string command, 
        string? workingDirectory, 
        int timeoutMs, 
        bool runInBackground,
        string? description,
        bool skipSecurityCheck,
        CancellationToken ct)
    {
        try
        {
            // Security analysis (unless explicitly skipped)
            if (!skipSecurityCheck)
            {
                var context = new ShellContext
                {
                    WorkingDirectory = workingDirectory,
                    IsSandboxed = _policy?.IsSandboxed ?? false,
                    AllowNetwork = _policy?.AllowNetwork ?? true,
                    AllowFileSystemWrite = _policy?.AllowFileSystemWrite ?? true,
                    AllowSubprocess = _policy?.AllowSubprocess ?? true,
                };
                
                var securityResult = _securityAnalyzer.Analyze(command, context);
                
                switch (securityResult.Classification)
                {
                    case SecurityClassification.Dangerous:
                        return ToolResult.Fail($"Security: {securityResult.Reason}");
                        
                    case SecurityClassification.TooComplex:
                        return ToolResult.Fail($"Security: Command too complex to analyze - {securityResult.Reason}");
                        
                    case SecurityClassification.NeedsReview:
                        // In auto mode, we could prompt user, but for now we log warning
                        if (_policy?.AutoApproveWarnings != true)
                        {
                            var warnings = string.Join("\n", securityResult.Warnings ?? new List<string> { securityResult.Reason ?? "Unknown" });
                            return ToolResult.Fail($"Security review required:\n{warnings}");
                        }
                        break;
                }
            }
            
            // Determine shell based on command
            var isPowerShell = command.StartsWith("pwsh") || command.StartsWith("powershell");
            var psi = new ProcessStartInfo
            {
                FileName = isPowerShell ? "powershell.exe" : "cmd.exe",
                Arguments = isPowerShell ? $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"" : $"/c {command}",
                WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = false,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            
            if (!string.IsNullOrEmpty(description))
            {
                Console.WriteLine($"[BASH] {description}");
            }
            
            using var process = new Process { StartInfo = psi };
            process.Start();
            
            if (runInBackground)
            {
                return ToolResult.Ok($"Command started in background (PID: {process.Id})");
            }
            
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
            
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var waitTask = Task.Run(() => process.WaitForExit(), timeoutCts.Token);
            
            await Task.WhenAll(stdoutTask, stderrTask, waitTask);
            
            var output = await stdoutTask;
            var error = await stderrTask;
            
            if (process.ExitCode == 0)
            {
                return ToolResult.Ok(string.IsNullOrEmpty(output) ? "Command completed successfully." : output);
            }
            else
            {
                return ToolResult.Fail($"Command failed with exit code {process.ExitCode}: {error}");
            }
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail($"Command timed out after {timeoutMs}ms");
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
    public bool SkipSecurityCheck { get; init; } = false;
}
