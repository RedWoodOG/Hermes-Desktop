namespace Hermes.Agent.Execution;

using System.Diagnostics;
using System.Text;

// ══════════════════════════════════════════════
// Modal Serverless Execution Backend
// ══════════════════════════════════════════════
//
// Upstream ref: tools/environments/modal.py
// Cloud sandbox execution via Modal CLI.
// Hibernates when idle — minimal cost.

public sealed class ModalBackend : IExecutionBackend
{
    private readonly ExecutionConfig _config;

    public ModalBackend(ExecutionConfig config) => _config = config;
    public ExecutionBackendType Type => ExecutionBackendType.Modal;

    public async Task<ExecutionResult> ExecuteAsync(
        string command, string? workingDirectory, int? timeoutMs,
        bool background, CancellationToken ct)
    {
        var timeout = timeoutMs ?? _config.DefaultTimeoutMs;
        var sw = Stopwatch.StartNew();

        // Use Modal CLI sandbox
        var appName = _config.ModalAppName ?? "hermes-sandbox";
        var args = $"shell {appName} --cmd \"{command.Replace("\"", "\\\"")}\"";

        if (workingDirectory is not null)
            args = $"shell {appName} --cmd \"cd '{workingDirectory}' && {command.Replace("\"", "\\\"")}\"";

        var psi = new ProcessStartInfo
        {
            FileName = "modal",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };

        try { process.Start(); }
        catch (Exception ex)
        {
            return new ExecutionResult
            {
                Output = $"Modal CLI not found or failed to start: {ex.Message}\nInstall with: pip install modal",
                ExitCode = -1,
                DurationMs = 0
            };
        }

        if (background)
        {
            sw.Stop();
            return new ExecutionResult
            {
                Output = $"Modal sandbox command started (PID: {process.Id})",
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
            try { process.Kill(entireProcessTree: true); } catch { }
            sw.Stop();
            return new ExecutionResult
            {
                Output = $"Modal command timed out after {timeout}ms",
                ExitCode = 124,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
