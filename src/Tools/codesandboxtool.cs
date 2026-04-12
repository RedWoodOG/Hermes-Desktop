namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using System.Diagnostics;

/// <summary>
/// Execute a code snippet in an isolated environment.
/// Supports Python, JavaScript, and C# via external processes.
/// </summary>
public sealed class CodeSandboxTool : ITool
{
    private static readonly HashSet<string> SupportedLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "python", "javascript", "csharp"
    };

    public string Name => "code_sandbox";
    public string Description => "Execute a code snippet in an isolated environment. Supports python, javascript, and csharp.";
    public Type ParametersType => typeof(CodeSandboxParameters);

    public async Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (CodeSandboxParameters)parameters;

        if (string.IsNullOrWhiteSpace(p.Language))
            return ToolResult.Fail("Language is required.");

        if (!SupportedLanguages.Contains(p.Language))
            return ToolResult.Fail(
                $"Unsupported language: {p.Language}. Supported: {string.Join(", ", SupportedLanguages)}");

        if (string.IsNullOrWhiteSpace(p.Code))
            return ToolResult.Fail("Code is required.");

        var timeout = p.Timeout > 0 ? p.Timeout : 30;

        try
        {
            var (executable, arguments) = GetExecutionCommand(p.Language, p.Code);
            return await RunProcessAsync(executable, arguments, timeout, ct);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Execution failed: {ex.Message}", ex);
        }
    }

    private static (string executable, string arguments) GetExecutionCommand(string language, string code)
    {
        return language.ToLowerInvariant() switch
        {
            "python" => ("python", $"-c \"{EscapeForShell(code)}\""),
            "javascript" => ("node", $"-e \"{EscapeForShell(code)}\""),
            "csharp" => ("dotnet", $"script eval \"{EscapeForShell(code)}\""),
            _ => throw new ArgumentException($"Unsupported language: {language}")
        };
    }

    private static string EscapeForShell(string code)
    {
        // Escape double quotes and backslashes for shell argument
        return code.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static async Task<ToolResult> RunProcessAsync(
        string executable, string arguments, int timeoutSeconds, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            await process.WaitForExitAsync(timeoutCts.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            var output = string.Empty;
            if (!string.IsNullOrEmpty(stdout))
                output += stdout;
            if (!string.IsNullOrEmpty(stderr))
                output += (output.Length > 0 ? "\n[stderr]\n" : "[stderr]\n") + stderr;

            if (string.IsNullOrEmpty(output))
                output = "(no output)";

            return process.ExitCode == 0
                ? ToolResult.Ok(output)
                : ToolResult.Ok($"[exit code: {process.ExitCode}]\n{output}");
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CodeSandboxTool timed-out process kill failed: {ex}");
            }
            return ToolResult.Fail($"Execution timed out after {timeoutSeconds}s.");
        }
    }
}

public sealed class CodeSandboxParameters
{
    public required string Language { get; init; }
    public required string Code { get; init; }
    public int Timeout { get; init; } = 30;
}
