namespace Hermes.Agent.Execution;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

// ══════════════════════════════════════════════
// Daytona Cloud Execution Backend
// ══════════════════════════════════════════════
//
// Upstream ref: tools/environments/daytona.py
// Cloud workspace execution via Daytona API.

public sealed class DaytonaBackend : IExecutionBackend
{
    private readonly ExecutionConfig _config;
    private readonly HttpClient _http;

    public DaytonaBackend(ExecutionConfig config)
    {
        _config = config;
        _http = new HttpClient();
        if (!string.IsNullOrWhiteSpace(config.DaytonaApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.DaytonaApiKey);
    }

    public ExecutionBackendType Type => ExecutionBackendType.Daytona;

    private const string ApiBase = "https://api.daytona.io/v1";

    public async Task<ExecutionResult> ExecuteAsync(
        string command, string? workingDirectory, int? timeoutMs,
        bool background, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.DaytonaApiKey))
            return new ExecutionResult
            {
                Output = "Daytona API key not configured. Set DAYTONA_API_KEY in config.yaml.",
                ExitCode = -1,
                DurationMs = 0
            };

        var timeout = timeoutMs ?? _config.DefaultTimeoutMs;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var workspaceId = _config.DaytonaWorkspaceId;

            // Create workspace if needed
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                var createPayload = new { name = "hermes-exec", target = "default" };
                var createResponse = await _http.PostAsJsonAsync($"{ApiBase}/workspaces", createPayload, ct);

                if (!createResponse.IsSuccessStatusCode)
                {
                    var error = await createResponse.Content.ReadAsStringAsync(ct);
                    return new ExecutionResult
                    {
                        Output = $"Failed to create Daytona workspace: {error}",
                        ExitCode = -1,
                        DurationMs = sw.ElapsedMilliseconds
                    };
                }

                var json = await createResponse.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                workspaceId = doc.RootElement.GetProperty("id").GetString();
            }

            // Execute command in workspace
            var execPayload = new
            {
                command,
                workdir = workingDirectory ?? "/workspace"
            };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            var execResponse = await _http.PostAsJsonAsync(
                $"{ApiBase}/workspaces/{workspaceId}/exec", execPayload, timeoutCts.Token);

            var execJson = await execResponse.Content.ReadAsStringAsync(timeoutCts.Token);
            sw.Stop();

            if (execResponse.IsSuccessStatusCode)
            {
                using var execDoc = JsonDocument.Parse(execJson);
                var output = execDoc.RootElement.TryGetProperty("output", out var outEl)
                    ? outEl.GetString() ?? "(no output)" : "(no output)";
                var exitCode = execDoc.RootElement.TryGetProperty("exit_code", out var ecEl)
                    ? ecEl.GetInt32() : 0;

                output = OutputTruncator.Truncate(output, _config.MaxOutputChars);

                return new ExecutionResult
                {
                    Output = output,
                    ExitCode = exitCode,
                    ExitCodeMeaning = ExitCodeInterpreter.Interpret(command, exitCode),
                    DurationMs = sw.ElapsedMilliseconds
                };
            }

            return new ExecutionResult
            {
                Output = $"Daytona exec failed: {execJson}",
                ExitCode = -1,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new ExecutionResult
            {
                Output = $"Daytona command timed out after {timeout}ms",
                ExitCode = 124,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ExecutionResult
            {
                Output = $"Daytona error: {ex.Message}",
                ExitCode = -1,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
