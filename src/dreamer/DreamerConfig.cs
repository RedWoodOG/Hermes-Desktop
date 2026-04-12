namespace Hermes.Agent.Dreamer;

using System.Globalization;
using Hermes.Agent.LLM;
using Microsoft.Extensions.Logging;

/// <summary>Configuration for the Dreamer background worker (dreamer: section in config.yaml).</summary>
public sealed class DreamerConfig
{
    public bool Enabled { get; set; }
    public string WalkProvider { get; set; } = "ollama";
    public string WalkModel { get; set; } = "qwen3.5:latest";
    public string WalkBaseUrl { get; set; } = "http://127.0.0.1:11434/v1";
    public double WalkTemperature { get; set; } = 1.1;
    public int WalkMaxTokens { get; set; } = 2048;
    public string BuildProvider { get; set; } = "openai";
    public string BuildModel { get; set; } = "gpt-5.4-mini";
    public string? BuildBaseUrl { get; set; }
    public int WalkIntervalMinutes { get; set; } = 30;
    public IReadOnlyList<string> DigestTimes { get; set; } = new[] { "08:00", "12:00", "20:00" };
    public string DiscordChannelId { get; set; } = "";
    public double TriggerThreshold { get; set; } = 7.0;
    public int MinWalksToTrigger { get; set; } = 4;
    public string Autonomy { get; set; } = "full"; // full | drafts | ideas
    public bool InputTranscripts { get; set; } = true;
    public bool InputInbox { get; set; } = true;
    public IReadOnlyList<string> RssFeeds { get; set; } = Array.Empty<string>();

    /// <summary>
            /// Resolves the base directory for Hermes configuration and data.
            /// </summary>
            /// <returns>`HERMES_HOME` environment variable value if it is set and non-empty; otherwise the path named "hermes" under the current user's LocalApplicationData folder.</returns>
            public static string ResolveHermesHome() =>
        Environment.GetEnvironmentVariable("HERMES_HOME") is { Length: > 0 } h
            ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes");

    /// <summary>Load dreamer: section from config.yaml (flat keys under dreamer:).</summary>
    public static DreamerConfig Load(string configPath, ILogger? logger = null)
    {
        var c = new DreamerConfig();
        Dictionary<string, string> kv;
        try
        {
            if (!File.Exists(configPath))
                return c;

            kv = ReadDreamerSection(configPath);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Failed to load Dreamer config from {Path}; using defaults", configPath);
            return c;
        }

        if (kv.Count == 0)
            return c;

        static bool ParseBool(string? v, bool def)
        {
            if (string.IsNullOrWhiteSpace(v)) return def;
            return v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   v.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                   v.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        c.Enabled = ParseBool(Get(kv, "enabled"), false);
        c.WalkProvider = Get(kv, "walk_provider") ?? c.WalkProvider;
        c.WalkModel = Get(kv, "walk_model") ?? c.WalkModel;
        c.WalkBaseUrl = Get(kv, "walk_base_url") ?? c.WalkBaseUrl;
        if (double.TryParse(Get(kv, "walk_temperature"), NumberStyles.Float, CultureInfo.InvariantCulture, out var wt))
            c.WalkTemperature = wt;
        if (int.TryParse(Get(kv, "walk_max_tokens"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var wmt))
            c.WalkMaxTokens = wmt;
        c.BuildProvider = Get(kv, "build_provider") ?? c.BuildProvider;
        c.BuildModel = Get(kv, "build_model") ?? c.BuildModel;
        c.BuildBaseUrl = Get(kv, "build_base_url");
        if (int.TryParse(Get(kv, "walk_interval_minutes"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var wim))
            c.WalkIntervalMinutes = Math.Clamp(wim, 1, 24 * 60);
        var digest = Get(kv, "digest_times");
        if (!string.IsNullOrWhiteSpace(digest))
        {
            var parts = digest.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var validated = new List<string>();
            foreach (var part in parts)
            {
                var timeParts = part.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (timeParts.Length == 2 &&
                    int.TryParse(timeParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) &&
                    int.TryParse(timeParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) &&
                    h >= 0 && h <= 23 && m >= 0 && m <= 59)
                {
                    validated.Add(part);
                }
            }
            c.DigestTimes = validated;
        }
        c.DiscordChannelId = Get(kv, "discord_channel_id") ?? "";
        if (double.TryParse(Get(kv, "trigger_threshold"), NumberStyles.Float, CultureInfo.InvariantCulture, out var th))
            c.TriggerThreshold = th;
        if (int.TryParse(Get(kv, "min_walks_to_trigger"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mwt))
            c.MinWalksToTrigger = Math.Max(1, mwt);
        c.Autonomy = Get(kv, "autonomy") ?? c.Autonomy;
        c.InputTranscripts = ParseBool(Get(kv, "input_transcripts"), true);
        c.InputInbox = ParseBool(Get(kv, "input_inbox"), true);
        var rss = Get(kv, "rss_feeds");
        if (!string.IsNullOrWhiteSpace(rss))
            c.RssFeeds = rss.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        return c;
    }

    /// <summary>
    /// Retrieve the trimmed value associated with a key from the dictionary.
    /// </summary>
    /// <param name="kv">The dictionary of key/value pairs to search.</param>
    /// <param name="key">The key to look up.</param>
    /// <returns>The trimmed value if the key exists; otherwise <c>null</c>.</returns>
    private static string? Get(Dictionary<string, string> kv, string key) =>
        kv.TryGetValue(key, out var v) ? v.Trim() : null;

    /// <summary>
    /// Extracts flat key/value pairs from the top-level <c>dreamer:</c> section of a configuration file.
    /// </summary>
    /// <param name="configPath">Path to the configuration file to read.</param>
    /// <returns>
    /// A case-insensitive dictionary mapping keys to their trimmed values (surrounding single or double quotes removed)
    /// found under the <c>dreamer:</c> block. Parsing stops when the next top-level key is encountered; returns an empty
    /// dictionary if the block is missing or contains no valid entries.
    /// </returns>
    private static Dictionary<string, string> ReadDreamerSection(string configPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool inDreamer = false;
        foreach (var raw in File.ReadAllLines(configPath))
        {
            var trimmed = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.TrimStart().StartsWith("#", StringComparison.Ordinal))
                continue;

            if (raw.Length > 0 && !char.IsWhiteSpace(raw[0]))
            {
                if (trimmed.Equals("dreamer:", StringComparison.OrdinalIgnoreCase))
                {
                    inDreamer = true;
                    continue;
                }

                if (inDreamer && trimmed.EndsWith(':'))
                    break;

                inDreamer = false;
                continue;
            }

            if (!inDreamer) continue;

            var line = trimmed;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var val = line[(colon + 1)..].Trim().Trim('"', '\'');
            result[key] = val;
        }

        return result;
    }

    /// <summary>
        /// Create an LlmConfig populated with this instance's walk model settings.
        /// </summary>
        /// <returns>An LlmConfig configured for the walk model: provider, model, base URL, temperature, and max tokens from this config; `ApiKey` is empty and `AuthMode` is "none".</returns>
        public LlmConfig ToWalkLlmConfig() =>
        new()
        {
            Provider = WalkProvider,
            Model = WalkModel,
            BaseUrl = WalkBaseUrl,
            Temperature = WalkTemperature,
            MaxTokens = WalkMaxTokens,
            ApiKey = "",
            AuthMode = "none"
        };

    /// <summary>
    /// Creates an "echo" LLM configuration that mirrors the walk model's provider, model, base URL, and API key but uses fixed echo-oriented parameters.
    /// </summary>
    /// <returns>An LlmConfig with the same Provider, Model, BaseUrl, and ApiKey as the walk config, Temperature = 0.2, MaxTokens = 1024, and AuthMode = "none".</returns>
    public LlmConfig ToEchoLlmConfig()
    {
        var w = ToWalkLlmConfig();
        return new LlmConfig
        {
            Provider = w.Provider,
            Model = w.Model,
            BaseUrl = w.BaseUrl,
            Temperature = 0.2,
            MaxTokens = 1024,
            ApiKey = w.ApiKey,
            AuthMode = "none"
        };
    }
}
