namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using System.Diagnostics;

public sealed class TerminalTool : ITool
{
    public string Name => "terminal";
    public string Description => "Execute shell commands on the local system";
    public Type ParametersType => typeof(TerminalParameters);
    
    public Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (TerminalParameters)parameters;
        return ExecuteCommandAsync(p.Command, p.WorkingDirectory, p.TimeoutSeconds, ct);
    }
    
    private async Task<ToolResult> ExecuteCommandAsync(string command, string? cwd, int timeout, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                WorkingDirectory = cwd ?? Directory.GetCurrentDirectory(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = false,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            
            using var process = new Process { StartInfo = psi };
            process.Start();
            
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));
            
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var waitTask = Task.Run(() => process.WaitForExit(), timeoutCts.Token);
            
            await Task.WhenAll(stdoutTask, stderrTask, waitTask);
            
            var output = await stdoutTask;
            var error = await stderrTask;
            
            return process.ExitCode == 0 
                ? ToolResult.Ok(string.IsNullOrEmpty(output) ? "Command completed successfully." : output)
                : ToolResult.Fail($"Command failed with exit code {process.ExitCode}: {error}");
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail($"Command timed out after {timeout} seconds");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to execute command: {ex.Message}", ex);
        }
    }
}

public sealed class TerminalParameters
{
    public required string Command { get; init; }
    public string? WorkingDirectory { get; init; }
    public int TimeoutSeconds { get; init; } = 60;
}
