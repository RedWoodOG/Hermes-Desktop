using System.Diagnostics;
using System.Text;

namespace Hermes.SmokeProbe;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        ProbeOptions options;
        try
        {
            options = ProbeOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(ProbeOptions.Usage);
            return 2;
        }

        ProbeResult result;
        try
        {
            result = await RunProbeAsync(options);
        }
        catch (Exception ex)
        {
            result = ProbeResult.Failed($"Unhandled exception while running smoke probe: {ex}");
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(options.ReportPath))
            {
                var reportDirectory = Path.GetDirectoryName(options.ReportPath);
                if (!string.IsNullOrEmpty(reportDirectory))
                {
                    Directory.CreateDirectory(reportDirectory);
                }

                await File.WriteAllTextAsync(options.ReportPath, result.ToReport(), Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to write report file '{options.ReportPath}': {ex}");
            return 1;
        }

        Console.WriteLine(result.ToReport());
        return result.Success ? 0 : 1;
    }

    private static async Task<ProbeResult> RunProbeAsync(ProbeOptions options)
    {
        if (!File.Exists(options.ExePath))
        {
            return ProbeResult.Failed($"Portable executable not found: {options.ExePath}");
        }

        var startupLogSnapshot = StartupLogSnapshot.Capture(options.StartupLogPath);
        var startedAt = DateTimeOffset.UtcNow;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = options.ExePath,
                WorkingDirectory = Path.GetDirectoryName(options.ExePath) ?? Environment.CurrentDirectory,
                UseShellExecute = false
            }
        };

        if (!process.Start())
        {
            return ProbeResult.Failed("Failed to start HermesDesktop.exe for smoke probe.");
        }

        var windowDetected = await WaitForMainWindowAsync(process, options.StartupTimeout);

        // Capture process exit state before any termination attempt so diagnostics
        // report the original launch outcome instead of post-kill state.
        bool hasExited = process.HasExited;
        int? exitCode = hasExited ? process.ExitCode : null;

        if (!windowDetected)
        {
            await TerminateProcessAsync(process);
            var missingWindowDetails = hasExited
                ? $"Process exited before creating a main window. Exit code: {exitCode?.ToString() ?? "unknown"}."
                : $"Timed out waiting {options.StartupTimeout.TotalSeconds:0}s for a main window.";

            var startupLogExcerpt = StartupLogSnapshot.ReadDelta(options.StartupLogPath, startupLogSnapshot);
            var detail = BuildFailureDetail(
                startedAt,
                process.Id,
                missingWindowDetails,
                startupLogExcerpt,
                fatalPatternHits: Array.Empty<string>());

            return ProbeResult.Failed(detail);
        }

        var settleDeadline = DateTime.UtcNow + options.SettleDelay;
        bool exitedDuringSettle = false;
        while (DateTime.UtcNow < settleDeadline)
        {
            if (process.HasExited)
            {
                exitedDuringSettle = true;
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        if (exitedDuringSettle)
        {
            var startupLogDelta = StartupLogSnapshot.ReadDelta(options.StartupLogPath, startupLogSnapshot);
            var detail = BuildFailureDetail(
                startedAt,
                process.Id,
                $"Process exited unexpectedly during settle period. Exit code: {process.ExitCode}.",
                startupLogDelta,
                fatalPatternHits: Array.Empty<string>());
            return ProbeResult.Failed(detail);
        }

        await TerminateProcessAsync(process);

        var appendedStartupLog = StartupLogSnapshot.ReadDelta(options.StartupLogPath, startupLogSnapshot);
        var fatalPatternHits = options.FatalPatterns
            .Where(pattern => appendedStartupLog.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (fatalPatternHits.Length > 0)
        {
            var detail = BuildFailureDetail(
                startedAt,
                process.Id,
                "Detected fatal startup markers in startup diagnostics log.",
                appendedStartupLog,
                fatalPatternHits);

            return ProbeResult.Failed(detail);
        }

        return ProbeResult.Passed(
            startedAt,
            process.Id,
            options.ExePath,
            options.StartupLogPath,
            windowAppeared: true,
            startupLogDeltaLength: appendedStartupLog.Length);
    }

    private static async Task<bool> WaitForMainWindowAsync(Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                return false;
            }

            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        return false;
    }

    private static async Task TerminateProcessAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.CloseMainWindow();
        }
        catch
        {
            // Best-effort close; fall back to Kill below.
        }

        using (var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
        {
            try
            {
                await process.WaitForExitAsync(closeCts.Token);
                return;
            }
            catch (OperationCanceledException)
            {
                // Fall through and force kill.
            }
        }

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            await process.WaitForExitAsync(killCts.Token);
        }
        catch (OperationCanceledException)
        {
            // If we still haven't exited there's no stronger action available here.
        }
    }

    private static string BuildFailureDetail(
        DateTimeOffset startedAt,
        int processId,
        string reason,
        string startupLogDelta,
        IReadOnlyCollection<string> fatalPatternHits)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Smoke probe failed.");
        builder.AppendLine($"UTC start time: {startedAt:O}");
        builder.AppendLine($"Process ID: {processId}");
        builder.AppendLine($"Reason: {reason}");

        if (fatalPatternHits.Count > 0)
        {
            builder.AppendLine($"Fatal pattern hits: {string.Join(", ", fatalPatternHits)}");
        }

        builder.AppendLine("Startup log delta:");
        if (string.IsNullOrWhiteSpace(startupLogDelta))
        {
            builder.AppendLine("(no new startup log content detected)");
        }
        else
        {
            builder.AppendLine(startupLogDelta.TrimEnd());
        }

        return builder.ToString().TrimEnd();
    }
}

internal sealed class ProbeOptions
{
    private ProbeOptions(
        string exePath,
        string startupLogPath,
        string reportPath,
        TimeSpan startupTimeout,
        TimeSpan settleDelay,
        string[] fatalPatterns)
    {
        ExePath = exePath;
        StartupLogPath = startupLogPath;
        ReportPath = reportPath;
        StartupTimeout = startupTimeout;
        SettleDelay = settleDelay;
        FatalPatterns = fatalPatterns;
    }

    internal static string Usage =>
        """
        Usage:
          Hermes.SmokeProbe --exe <path> --startup-log <path> [options]

        Options:
          --report-path <path>                Optional report output file
          --startup-timeout-seconds <n>       Defaults to 90
          --settle-seconds <n>                Defaults to 8
          --fatal-pattern <text>              Repeat to add more fatal markers
        """;

    internal string ExePath { get; }

    internal string StartupLogPath { get; }

    internal string ReportPath { get; }

    internal TimeSpan StartupTimeout { get; }

    internal TimeSpan SettleDelay { get; }

    internal IReadOnlyList<string> FatalPatterns { get; }

    internal static ProbeOptions Parse(string[] args)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{key}'.");
            }

            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for '{key}'.");
            }

            var value = args[++i];
            if (!map.TryGetValue(key, out var values))
            {
                values = new List<string>();
                map[key] = values;
            }

            values.Add(value);
        }

        string exePath = RequiredSingle(map, "--exe");
        string startupLogPath = RequiredSingle(map, "--startup-log");
        string reportPath = OptionalSingle(map, "--report-path");

        var startupTimeoutSeconds = ParseInt(map, "--startup-timeout-seconds", 90);
        var settleSeconds = ParseInt(map, "--settle-seconds", 8);
        if (startupTimeoutSeconds <= 0)
        {
            throw new ArgumentException("--startup-timeout-seconds must be greater than zero.");
        }

        if (settleSeconds < 0)
        {
            throw new ArgumentException("--settle-seconds cannot be negative.");
        }

        var fatalPatterns = map.TryGetValue("--fatal-pattern", out var providedPatterns) && providedPatterns.Count > 0
            ? providedPatterns.ToArray()
            : new[]
            {
                "Fatal startup error",
                "constructor failed",
                "Cannot create instance of type ReplayPanel",
                "XamlParseException"
            };

        return new ProbeOptions(
            Path.GetFullPath(exePath),
            Path.GetFullPath(startupLogPath),
            string.IsNullOrWhiteSpace(reportPath) ? string.Empty : Path.GetFullPath(reportPath),
            TimeSpan.FromSeconds(startupTimeoutSeconds),
            TimeSpan.FromSeconds(settleSeconds),
            fatalPatterns);
    }

    private static string RequiredSingle(Dictionary<string, List<string>> map, string key)
    {
        if (!map.TryGetValue(key, out var values) || values.Count == 0 || string.IsNullOrWhiteSpace(values[0]))
        {
            throw new ArgumentException($"Missing required argument '{key}'.");
        }

        return values[0];
    }

    private static string OptionalSingle(Dictionary<string, List<string>> map, string key)
    {
        if (!map.TryGetValue(key, out var values) || values.Count == 0)
        {
            return string.Empty;
        }

        return values[0];
    }

    private static int ParseInt(Dictionary<string, List<string>> map, string key, int fallback)
    {
        if (!map.TryGetValue(key, out var values) || values.Count == 0)
        {
            return fallback;
        }

        if (!int.TryParse(values[0], out var parsed))
        {
            throw new ArgumentException($"Argument '{key}' must be an integer.");
        }

        return parsed;
    }
}

internal readonly record struct StartupLogSnapshot(bool Exists, long Length)
{
    internal static StartupLogSnapshot Capture(string path)
    {
        if (!File.Exists(path))
        {
            return new StartupLogSnapshot(false, 0);
        }

        return new StartupLogSnapshot(true, new FileInfo(path).Length);
    }

    internal static string ReadDelta(string path, StartupLogSnapshot snapshot)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        var currentLength = new FileInfo(path).Length;
        if (!snapshot.Exists || currentLength < snapshot.Length)
        {
            return File.ReadAllText(path);
        }

        if (currentLength == snapshot.Length)
        {
            return string.Empty;
        }

        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(snapshot.Length, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}

internal sealed record ProbeResult(bool Success, string Message)
{
    internal static ProbeResult Failed(string message) => new(false, message);

    internal static ProbeResult Passed(
        DateTimeOffset startedAt,
        int processId,
        string exePath,
        string startupLogPath,
        bool windowAppeared,
        int startupLogDeltaLength)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Smoke probe passed.");
        builder.AppendLine($"UTC start time: {startedAt:O}");
        builder.AppendLine($"Process ID: {processId}");
        builder.AppendLine($"Executable: {exePath}");
        builder.AppendLine($"Startup log path: {startupLogPath}");
        builder.AppendLine($"Main window observed: {windowAppeared}");
        builder.AppendLine($"Startup log delta length: {startupLogDeltaLength}");
        return new ProbeResult(true, builder.ToString().TrimEnd());
    }

    internal string ToReport() => Message;
}
