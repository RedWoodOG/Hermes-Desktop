using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Hermes.Agent.Security;

namespace HermesDesktop.Services;

/// <summary>Builds a redacted diagnostics bundle for support (clipboard).</summary>
internal static class DiagnosticsReportBuilder
{
    internal static string BuildReport(bool includeStartupLogTail)
    {
        var sb = new StringBuilder(8192);
        sb.AppendLine("=== Hermes Desktop diagnostics ===");
        sb.AppendLine();
        AppendHeader(sb);
        sb.AppendLine();
        AppendPaths(sb);
        sb.AppendLine();
        AppendRuntime(sb);

        if (includeStartupLogTail)
        {
            sb.AppendLine();
            sb.AppendLine("--- desktop-startup.log (tail) ---");
            sb.AppendLine(ReadStartupLogTail(HermesEnvironment.DesktopStartupLogPath, maxBytes: 48 * 1024));
        }

        return SecretScanner.RedactSecrets(sb.ToString());
    }

    internal static string GetRedactedStartupLogTailForDisplay(int maxBytes = 48 * 1024) =>
        SecretScanner.RedactSecrets(
            ReadStartupLogTail(HermesEnvironment.DesktopStartupLogPath, maxBytes));

    private static void AppendHeader(StringBuilder sb)
    {
        string ver = typeof(HermesDesktop.App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(HermesDesktop.App).Assembly.GetName().Version?.ToString()
            ?? "unknown";
        sb.AppendLine("App: Hermes Desktop");
        sb.AppendLine($"Version: {ver}");
        sb.AppendLine(FormattableString.Invariant($"Timestamp (UTC): {DateTimeOffset.UtcNow:O}"));
    }

    private static void AppendPaths(StringBuilder sb)
    {
        sb.AppendLine("--- Paths ---");
        sb.AppendLine($"HERMES_HOME (effective): {HermesEnvironment.DisplayHermesHomePath}");
        sb.AppendLine($"Desktop log directory: {HermesEnvironment.DisplayDesktopCsLogsDirectory}");
        sb.AppendLine($"Desktop startup log: {HermesEnvironment.DisplayDesktopStartupLogPath}");
        bool exists = File.Exists(HermesEnvironment.DesktopStartupLogPath);
        sb.AppendLine($"Startup log exists: {exists}");
        if (exists)
        {
            long len = new FileInfo(HermesEnvironment.DesktopStartupLogPath).Length;
            sb.AppendLine($"Startup log size (bytes): {len.ToString(CultureInfo.InvariantCulture)}");
        }

        sb.AppendLine($"Privacy / redacted UI paths mode: {HermesEnvironment.PrivacyModeEnabled}");
        sb.AppendLine($"HERMES_DESKTOP_SHOW_LOCAL_DETAILS: {Environment.GetEnvironmentVariable("HERMES_DESKTOP_SHOW_LOCAL_DETAILS") ?? "(unset)"}");
    }

    private static void AppendRuntime(StringBuilder sb)
    {
        sb.AppendLine("--- Runtime ---");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"OS architecture: {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"Framework: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Process architecture: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"64-bit process: {Environment.Is64BitProcess}");
    }

    private static string ReadStartupLogTail(string path, int maxBytes)
    {
        if (!File.Exists(path))
            return "(file not found)";

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int toRead = (int)Math.Min(maxBytes, fs.Length);
            if (toRead <= 0)
                return "(empty)";

            fs.Seek(-toRead, SeekOrigin.End);
            var buffer = new byte[toRead];
            int read = fs.Read(buffer, 0, toRead);
            string text = Encoding.UTF8.GetString(buffer, 0, read);
            int nl = text.IndexOf('\n');
            if (nl is >= 0 and < 160)
                text = text[(nl + 1)..];

            return text.TrimEnd();
        }
        catch (Exception ex)
        {
            return $"(failed to read: {ex.Message})";
        }
    }
}
