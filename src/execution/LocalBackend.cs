namespace Hermes.Agent.Execution;

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

// ══════════════════════════════════════════════
// Local Execution Backend
// ══════════════════════════════════════════════
//
// Upstream ref: tools/environments/local.py
// Executes commands directly on the host machine.

public sealed partial class LocalBackend : IExecutionBackend
{
    private readonly ExecutionConfig _config;

    public LocalBackend(ExecutionConfig config) => _config = config;
    public ExecutionBackendType Type => ExecutionBackendType.Local;

    public async Task<ExecutionResult> ExecuteAsync(
        string command, string? workingDirectory, int? timeoutMs,
        bool background, CancellationToken ct)
    {
        var timeout = timeoutMs ?? _config.DefaultTimeoutMs;
        var sw = Stopwatch.StartNew();

        // Determine shell
        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/bash",
            Arguments = isWindows ? $"/c {command}" : $"-c {command}",
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
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
                Output = $"Command started in background (PID: {process.Id})",
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

            // Combine and strip ANSI
            var output = string.IsNullOrEmpty(stderr)
                ? stdout
                : $"{stdout}\n{stderr}";
            output = StripAnsi(output);
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
                System.Diagnostics.Debug.WriteLine($"LocalBackend timed-out process kill failed: {ex}");
            }
            sw.Stop();
            return new ExecutionResult
            {
                Output = $"Command timed out after {timeout}ms",
                ExitCode = 124,
                ExitCodeMeaning = "Timed out",
                DurationMs = sw.ElapsedMilliseconds
            };
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string StripAnsi(string text) =>
        AnsiRegex().Replace(text, "");

    [GeneratedRegex(@"\x1B\[[0-9;]*[a-zA-Z]|\x1B\].*?\x07|\x1B[()][AB012]")]
    private static partial Regex AnsiRegex();
}
