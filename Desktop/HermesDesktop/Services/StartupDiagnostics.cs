using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace HermesDesktop.Services;

internal static class StartupDiagnostics
{
    private const uint MessageBoxOk = 0x00000000;
    private const uint MessageBoxIconError = 0x00000010;
    private const uint MessageBoxSetForeground = 0x00010000;

    internal static void ReportFatalStartupException(Exception exception)
    {
        string[] overlayProcesses = GetOverlayProcesses();
        string logPath = WriteStartupLog(exception, overlayProcesses);
        TryShowStartupMessage(exception, logPath, overlayProcesses);
    }

    /// <summary>
    /// Append a control-construction failure to the startup log. WinUI's XAML loader
    /// collapses any exception thrown from a UserControl constructor into an opaque
    /// XamlParseException with no InnerException once the failure crosses the WinRT
    /// ABI, so panels that wrap their ctor in try/catch should call this before
    /// rethrowing to make the underlying root cause visible in crash reports.
    /// </summary>
    internal static void LogControlConstructorFailure(string controlName, Exception exception)
    {
        try
        {
            string logPath = GetStartupLogPath();
            StringBuilder builder = new();
            builder.AppendLine($"[{DateTimeOffset.Now:O}] {controlName} constructor failed");
            builder.AppendLine($"Type: {exception.GetType().FullName}");
            builder.AppendLine($"Message: {exception.Message}");
            builder.AppendLine("Stack:");
            builder.AppendLine(exception.ToString());
            builder.AppendLine(new string('-', 80));
            File.AppendAllText(logPath, builder.ToString());
        }
        catch (Exception loggingEx)
        {
            // Logging is best-effort; never let the diagnostic path swallow the real
            // failure or throw a secondary exception that masks it.
            Debug.WriteLine($"StartupDiagnostics.LogControlConstructorFailure failed for {controlName}: {loggingEx}");
        }
    }

    private static string WriteStartupLog(Exception exception, string[] overlayProcesses)
    {
        string logPath = GetStartupLogPath();
        StringBuilder builder = new();

        builder.AppendLine($"[{DateTimeOffset.Now:O}] Fatal startup error");
        builder.AppendLine($"Type: {exception.GetType().FullName}");
        builder.AppendLine($"Message: {exception.Message}");

        if (overlayProcesses.Length > 0)
        {
            builder.AppendLine($"Detected overlay/injection processes: {string.Join(", ", overlayProcesses)}");
        }

        builder.AppendLine("Stack:");
        builder.AppendLine(exception.ToString());
        builder.AppendLine(new string('-', 80));

        File.AppendAllText(logPath, builder.ToString());
        return logPath;
    }

    private static string GetStartupLogPath()
    {
        try
        {
            string logsDir = Path.Combine(HermesEnvironment.HermesHomePath, "hermes-cs", "logs");
            Directory.CreateDirectory(logsDir);
            return Path.Combine(logsDir, "desktop-startup.log");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"StartupDiagnostics.GetStartupLogPath fallback triggered: {ex}");
            string fallbackDir = Path.Combine(Path.GetTempPath(), "HermesDesktop");
            Directory.CreateDirectory(fallbackDir);
            return Path.Combine(fallbackDir, "desktop-startup.log");
        }
    }

    private static string[] GetOverlayProcesses()
    {
        return new[] { "RTSS", "MSIAfterburner" }
            .Where(name =>
            {
                try
                {
                    return Process.GetProcessesByName(name).Length > 0;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"StartupDiagnostics overlay process probe failed for {name}: {ex}");
                    return false;
                }
            })
            .ToArray();
    }

    private static void TryShowStartupMessage(Exception exception, string logPath, string[] overlayProcesses)
    {
        try
        {
            string overlayHint = overlayProcesses.Length > 0
                ? $"\n\nDetected overlay/injection software: {string.Join(", ", overlayProcesses)}. Try closing it and launching Hermes Desktop again."
                : string.Empty;

            MessageBoxW(
                nint.Zero,
                $"Hermes Desktop failed to start.\n\n{exception.GetType().Name}: {exception.Message}\n\nStartup details were written to:\n{logPath}{overlayHint}",
                "Hermes Desktop",
                MessageBoxOk | MessageBoxIconError | MessageBoxSetForeground);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"StartupDiagnostics message box display failed: {ex}");
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);
}
