namespace Hermes.Agent.Execution;

using System.Diagnostics;
using System.Text;

// ══════════════════════════════════════════════
// SSH Remote Execution Backend
// ══════════════════════════════════════════════
//
// Upstream ref: tools/environments/ssh.py
// Remote command execution over SSH using the system ssh client.

public sealed class SshBackend : IExecutionBackend
{
    private readonly ExecutionConfig _config;

    public SshBackend(ExecutionConfig config) => _config = config;
    public ExecutionBackendType Type => ExecutionBackendType.Ssh;

    public async Task<ExecutionResult> ExecuteAsync(
        string command, string? workingDirectory, int? timeoutMs,
        bool background, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.SshHost))
            return new ExecutionResult { Output = "SSH host not configured", ExitCode = -1 };
        if (string.IsNullOrWhiteSpace(_config.SshUser))
            return new ExecutionResult { Output = "SSH user not configured", ExitCode = -1 };

        var timeout = timeoutMs ?? _config.DefaultTimeoutMs;
        var sw = Stopwatch.StartNew();

        // Build SSH command
        var sshArgs = new StringBuilder();
        if (_config.SshKeyPath is not null)
            sshArgs.Append($"-i \"{_config.SshKeyPath}\" ");
        if (_config.SshPort != 22)
            sshArgs.Append($"-p {_config.SshPort} ");
        sshArgs.Append("-o StrictHostKeyChecking=accept-new ");
        sshArgs.Append($"{_config.SshUser}@{_config.SshHost} ");

        // Wrap command with optional cd
        var remoteCmd = workingDirectory is not null
            ? $"cd '{workingDirectory}' && {command}"
            : command;

        // Shell-quote the remote command
        sshArgs.Append($"'{remoteCmd.Replace("'", "'\\''")}'");

        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            Arguments = sshArgs.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        if (background)
        {
            sw.Stop();
            return new ExecutionResult
            {
                Output = $"SSH command started (PID: {process.Id})",
                ExitCode = 0,
                DurationMs = sw.ElapsedMilliseconds,
                BackgroundProcessId = process.Id.ToString()
            };
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            var stdout = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderr = await process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            sw.Stop();

            var output = string.IsNullOrEmpty(stderr) ? stdout : $"{stdout}\n{stderr}";
            output = OutputTruncator.Truncate(output, _config.MaxOutputChars);

            return new ExecutionResult
            {
                Output = string.IsNullOrWhiteSpace(output) ? "(no output)" : output,
                ExitCode = process.ExitCode,
                ExitCodeMeaning = ExitCodeInterpreter.Interpret(command, process.ExitCode),
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SshBackend timed-out process kill failed: {ex}");
            }
            sw.Stop();
            return new ExecutionResult
            {
                Output = $"SSH command timed out after {timeout}ms",
                ExitCode = 124,
                ExitCodeMeaning = "Timed out",
                DurationMs = sw.ElapsedMilliseconds
            };
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
