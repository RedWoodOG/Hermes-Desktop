namespace Hermes.Agent.Execution;

using System.Diagnostics;
using System.Text;

// ══════════════════════════════════════════════
// Docker Execution Backend
// ══════════════════════════════════════════════
//
// Upstream ref: tools/environments/docker.py
// Containerized execution with resource limits, volume mounting,
// and environment variable forwarding.

public sealed class DockerBackend : IExecutionBackend
{
    private readonly ExecutionConfig _config;
    private string? _containerId;

    public DockerBackend(ExecutionConfig config) => _config = config;
    public ExecutionBackendType Type => ExecutionBackendType.Docker;

    public async Task<ExecutionResult> ExecuteAsync(
        string command, string? workingDirectory, int? timeoutMs,
        bool background, CancellationToken ct)
    {
        var timeout = timeoutMs ?? _config.DefaultTimeoutMs;
        var image = _config.DockerImage ?? "ubuntu:latest";
        var sw = Stopwatch.StartNew();

        // Build docker run command
        var args = new StringBuilder("run --rm");

        // Working directory
        if (workingDirectory is not null)
            args.Append($" -w \"{workingDirectory}\"");

        // Volumes
        foreach (var vol in _config.DockerVolumes)
            args.Append($" -v \"{vol}\"");

        // Mount current directory if no explicit volumes
        if (_config.DockerVolumes.Count == 0)
        {
            var cwd = workingDirectory ?? Directory.GetCurrentDirectory();
            args.Append($" -v \"{cwd}:{cwd}\" -w \"{cwd}\"");
        }

        // Environment variables
        foreach (var (key, value) in _config.DockerEnv)
            args.Append($" -e \"{key}={value}\"");

        // Resource limits
        args.Append(" --memory=2g --cpus=2");

        // Network (disable for sandboxed mode)
        // args.Append(" --network=none"); // Uncomment for full sandbox

        args.Append($" {image} /bin/bash -c \"{command.Replace("\"", "\\\"")}\"");

        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = args.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };
        process.Start();
        _containerId = null; // `docker run --rm` auto-cleans

        if (background)
        {
            sw.Stop();
            return new ExecutionResult
            {
                Output = $"Docker container started (PID: {process.Id})",
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
                System.Diagnostics.Debug.WriteLine($"DockerBackend timed-out process kill failed: {ex}");
            }
            sw.Stop();
            return new ExecutionResult
            {
                Output = $"Docker command timed out after {timeout}ms",
                ExitCode = 124,
                ExitCodeMeaning = "Timed out",
                DurationMs = sw.ElapsedMilliseconds
            };
        }
    }

    public async Task CleanupAsync()
    {
        if (_containerId is null) return;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"rm -f {_containerId}",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is not null) await p.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DockerBackend cleanup failed for container {_containerId}: {ex}");
        }
    }

    public async ValueTask DisposeAsync() => await CleanupAsync();
}
