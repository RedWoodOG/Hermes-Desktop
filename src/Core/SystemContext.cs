namespace Hermes.Agent.Core;

/// <summary>
/// Layered system instructions assembled for a single model call.
/// Each named layer is optional. Providers render this according to their
/// own protocol — a single leading system message (OpenAI), the top-level
/// system array (Anthropic), the systemInstruction field (Gemini), etc.
///
/// Replaces the previous practice of emitting multiple <c>role: "system"</c>
/// entries directly into the conversation message list, which was correct
/// for permissive backends (OpenAI, Anthropic, Ollama) but produced
/// spec-noncompliant payloads for strict OpenAI-compatible servers
/// (vLLM with Qwen / Llama-3 chat templates, llama.cpp strict templates,
/// TGI, several LMStudio templates) that enforce "exactly one system
/// message at index 0."
///
/// Naming layers explicitly enables per-layer prompt caching, per-layer
/// token accounting, and selective dropping under context pressure — all
/// of which are awkward when system content is opaque text.
/// </summary>
public sealed record SystemContext
{
    public string? Soul { get; init; }
    public string? Persona { get; init; }
    public string? SessionState { get; init; }
    public string? Wiki { get; init; }
    public string? Plugins { get; init; }
    public string? Memory { get; init; }

    /// <summary>
    /// Per-turn injections that don't belong to a stable named layer.
    /// Cleared between turns by the agent loop.
    /// </summary>
    public IReadOnlyList<string> Transient { get; init; } = Array.Empty<string>();

    public static SystemContext Empty { get; } = new();

    public bool IsEmpty => !NonEmptyLayers().Any();

    /// <summary>
    /// Layers in canonical render order, skipping null/whitespace entries.
    /// Each tuple is (layer name, trimmed content). Providers iterate this
    /// when serializing the request.
    /// </summary>
    public IEnumerable<(string Name, string Content)> NonEmptyLayers()
    {
        if (!string.IsNullOrWhiteSpace(Soul)) yield return ("soul", Soul!.Trim());
        if (!string.IsNullOrWhiteSpace(Persona)) yield return ("persona", Persona!.Trim());
        if (!string.IsNullOrWhiteSpace(SessionState)) yield return ("sessionState", SessionState!.Trim());
        if (!string.IsNullOrWhiteSpace(Wiki)) yield return ("wiki", Wiki!.Trim());
        if (!string.IsNullOrWhiteSpace(Plugins)) yield return ("plugins", Plugins!.Trim());
        if (!string.IsNullOrWhiteSpace(Memory)) yield return ("memory", Memory!.Trim());
        for (int i = 0; i < Transient.Count; i++)
        {
            var t = Transient[i];
            if (!string.IsNullOrWhiteSpace(t)) yield return ($"transient[{i}]", t.Trim());
        }
    }

    /// <summary>
    /// Concatenates every non-empty layer with the given separator.
    /// Default is two newlines — appropriate for OpenAI-style single-system
    /// rendering. Anthropic and other providers can use their own separators
    /// or render layers as separate blocks.
    /// </summary>
    public string Render(string separator = "\n\n")
        => string.Join(separator, NonEmptyLayers().Select(l => l.Content));

    /// <summary>
    /// Migration bridge used while call sites are being moved off the
    /// legacy practice of embedding role:"system" entries in the message
    /// list. Splits a legacy message list into a SystemContext (everything
    /// system-role lands in Transient, in original order) and a system-free
    /// conversation list. Removed alongside the legacy IChatClient overloads
    /// once the migration completes.
    /// </summary>
    public static (SystemContext System, List<Message> Conversation) FromLegacyMessages(
        IEnumerable<Message> messages)
    {
        var transient = new List<string>();
        var conversation = new List<Message>();
        foreach (var m in messages)
        {
            if (m.Role == "system")
            {
                if (!string.IsNullOrWhiteSpace(m.Content)) transient.Add(m.Content);
                continue;
            }
            conversation.Add(m);
        }
        return (new SystemContext { Transient = transient }, conversation);
    }
}
