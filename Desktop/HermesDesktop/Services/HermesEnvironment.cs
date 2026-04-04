using System;
using System.Diagnostics;
using System.IO;
using System.Globalization;

namespace HermesDesktop.Services;

internal static class HermesEnvironment
{
    internal static bool ShowLocalDetailsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("HERMES_DESKTOP_SHOW_LOCAL_DETAILS"),
            "1",
            StringComparison.Ordinal) ||
        File.Exists(Path.Combine(AppContext.BaseDirectory, "show-local-details.flag"));

    internal static bool PrivacyModeEnabled => !ShowLocalDetailsEnabled;

    internal static string HermesHomePath =>
        Environment.GetEnvironmentVariable("HERMES_HOME") is { Length: > 0 } configuredHome
            ? configuredHome
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes");

    internal static string HermesConfigPath => Path.Combine(HermesHomePath, "config.yaml");

    internal static string HermesLogsPath => Path.Combine(HermesHomePath, "logs");

    internal static string HermesWorkspacePath => Path.Combine(HermesHomePath, "hermes-agent");

    internal static string HermesCommandPath => Path.Combine(HermesHomePath, "bin", "hermes.cmd");

    internal static bool HermesInstalled => File.Exists(HermesCommandPath);

    /// <summary>Create LlmConfig from config.yaml for DI.</summary>
    internal static Hermes.Agent.LLM.LlmConfig CreateLlmConfig() => new()
    {
        Provider = ModelProvider,
        Model = DefaultModel,
        BaseUrl = ModelBaseUrl,
        ApiKey = ModelApiKey ?? ""
    };

    internal static bool TelegramConfigured => HasEnvironmentVariable("TELEGRAM_BOT_TOKEN");

    internal static bool DiscordConfigured => HasEnvironmentVariable("DISCORD_BOT_TOKEN");

    internal static bool HasAnyMessagingToken => TelegramConfigured || DiscordConfigured;

    internal static string ModelProvider => ReadModelSetting("provider") ?? "custom";

    internal static string ModelBaseUrl => ReadModelSetting("base_url") ?? "http://127.0.0.1:11434/v1";

    internal static string DefaultModel => ReadModelSetting("default") ?? "minimax-m2.7:cloud";

    internal static string DisplayModelProvider =>
        PrivacyModeEnabled ? "configured" : ModelProvider;

    internal static string DisplayModelBaseUrl =>
        PrivacyModeEnabled ? "Configured local endpoint" : FormatPathForDisplay(ModelBaseUrl);

    internal static string DisplayDefaultModel =>
        PrivacyModeEnabled ? "configured local model" : DefaultModel;

    internal static string DisplayHermesHomePath =>
        PrivacyModeEnabled ? "Local application data\\hermes" : FormatPathForDisplay(HermesHomePath);

    internal static string DisplayHermesConfigPath =>
        PrivacyModeEnabled ? "Local application data\\hermes\\config.yaml" : FormatPathForDisplay(HermesConfigPath);

    internal static string DisplayHermesLogsPath =>
        PrivacyModeEnabled ? "Local application data\\hermes\\logs" : FormatPathForDisplay(HermesLogsPath);

    internal static string DisplayHermesWorkspacePath =>
        PrivacyModeEnabled ? "Embedded Hermes workspace" : FormatPathForDisplay(HermesWorkspacePath);

    internal static string DisplayAgentWorkingDirectory =>
        PrivacyModeEnabled ? "Active project workspace" : FormatPathForDisplay(AgentWorkingDirectory);

    internal static string DisplayModelPort =>
        PrivacyModeEnabled ? "Configured" : ModelPortDisplay;

    internal static string? ModelApiKey => ReadModelSetting("api_key");

    internal static string AgentWorkingDirectory
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable("HERMES_DESKTOP_WORKSPACE");
            if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            {
                return configured;
            }

            string candidate = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                ".."));

            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }

    internal static string ModelPortDisplay
    {
        get
        {
            if (Uri.TryCreate(ModelBaseUrl, UriKind.Absolute, out Uri? uri))
            {
                return uri.Port.ToString(CultureInfo.CurrentCulture);
            }

            return "11434";
        }
    }

    internal static void LaunchHermesChat()
    {
        if (!HermesInstalled)
        {
            OpenHermesHome();
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoExit -ExecutionPolicy Bypass -Command \"& '{HermesCommandPath}' chat\"",
            UseShellExecute = true,
        });
    }

    internal static void OpenHermesHome()
    {
        OpenPath(HermesHomePath);
    }

    internal static void OpenConfig()
    {
        OpenPath(File.Exists(HermesConfigPath) ? HermesConfigPath : HermesHomePath);
    }

    internal static void OpenLogs()
    {
        OpenPath(Directory.Exists(HermesLogsPath) ? HermesLogsPath : HermesHomePath);
    }

    internal static void OpenWorkspace()
    {
        OpenPath(Directory.Exists(HermesWorkspacePath) ? HermesWorkspacePath : HermesHomePath);
    }

    private static bool HasEnvironmentVariable(string key)
    {
        return Environment.GetEnvironmentVariable(key) is { Length: > 0 };
    }

    private static void OpenPath(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }

    private static string FormatPathForDisplay(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile) &&
            value.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
        {
            return "~" + value[userProfile.Length..];
        }

        return value;
    }

    private static string? ReadModelSetting(string key)
    {
        if (!File.Exists(HermesConfigPath))
        {
            return null;
        }

        bool inModelSection = false;

        foreach (string rawLine in File.ReadLines(HermesConfigPath))
        {
            string line = rawLine.TrimEnd();

            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            if (!char.IsWhiteSpace(rawLine, 0) && line.EndsWith(":", StringComparison.Ordinal))
            {
                inModelSection = string.Equals(line, "model:", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inModelSection)
            {
                continue;
            }

            string trimmed = line.Trim();
            string prefix = $"{key}:";

            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return trimmed[prefix.Length..].Trim().Trim('"', '\'');
        }

        return null;
    }
}
