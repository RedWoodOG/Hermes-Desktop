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
