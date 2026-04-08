namespace Hermes.Agent.LLM;

using System.Text.RegularExpressions;

// ══════════════════════════════════════════════
// Smart Model Router
// ══════════════════════════════════════════════
//
// Upstream ref: agent/smart_model_routing.py
// Routes simple messages to cheaper models automatically.
// 32 complexity keywords, length thresholds, code/URL detection.
// Config-disabled by default.

/// <summary>
/// Evaluates message complexity to decide whether a cheaper model suffices.
/// Conservative — only routes genuinely simple messages to save cost.
/// </summary>
public sealed class ModelRouter
{
    // Upstream: 32 keywords indicating complex work
    private static readonly HashSet<string> ComplexKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "debug", "implement", "refactor", "exception", "analyze", "architecture",
        "optimize", "test", "docker", "deploy", "migration", "security", "database",
        "performance", "async", "concurrent", "thread", "memory", "leak", "crash",
        "regression", "integration", "infrastructure", "kubernetes", "ci", "cd",
        "pipeline", "terraform", "aws", "azure", "gcp", "api", "endpoint"
    };

    private static readonly Regex UrlPattern = new(
        @"https?://\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Configuration for smart routing.
    /// </summary>
    public sealed class RouterConfig
    {
        public bool Enabled { get; set; }
        public string? CheapModel { get; set; }
        public string? CheapProvider { get; set; }
        public int MaxSimpleChars { get; set; } = 160;
        public int MaxSimpleWords { get; set; } = 28;
    }

    /// <summary>
    /// Routing decision result.
    /// </summary>
    public sealed class RouteDecision
    {
        public required bool UseCheapModel { get; init; }
        public string? Model { get; init; }
        public string? Provider { get; init; }
        public string? Reason { get; init; }

        public static RouteDecision Primary(string reason) => new()
        {
            UseCheapModel = false, Reason = reason
        };

        public static RouteDecision Cheap(string model, string? provider) => new()
        {
            UseCheapModel = true, Model = model, Provider = provider, Reason = "simple_turn"
        };
    }

    /// <summary>
    /// Decide whether to route to a cheap model.
    /// Returns null if routing is disabled or not applicable.
    /// </summary>
    public static RouteDecision? Evaluate(string message, RouterConfig config)
    {
        if (!config.Enabled) return null;
        if (string.IsNullOrWhiteSpace(config.CheapModel)) return null;

        // Length check
        if (message.Length > config.MaxSimpleChars)
            return RouteDecision.Primary("message_too_long");

        var words = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > config.MaxSimpleWords)
            return RouteDecision.Primary("too_many_words");

        // Newline check (>1 newline = structured/complex)
        if (message.Count(c => c == '\n') > 1)
            return RouteDecision.Primary("multiline");

        // Code block detection
        if (message.Contains('`'))
            return RouteDecision.Primary("contains_code");

        // URL detection
        if (UrlPattern.IsMatch(message))
            return RouteDecision.Primary("contains_url");

        // Complexity keyword check
        var normalized = message.ToLowerInvariant();
        // Strip punctuation for word matching
        var cleanWords = normalized.Split(
            new[] { ' ', ',', '.', '!', '?', ';', ':', '(', ')', '[', ']', '{', '}' },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in cleanWords)
        {
            if (ComplexKeywords.Contains(word))
                return RouteDecision.Primary($"complex_keyword:{word}");
        }

        // All checks passed — this is a simple message
        return RouteDecision.Cheap(config.CheapModel, config.CheapProvider);
    }
}
